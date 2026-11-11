using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed record BackgroundRecordingStatus(string State,string File,string Message,double DurationSeconds=0,long Frames=0,long DroppedFrames=0,bool? Audio=null,int Tick=0,int TotalTicks=0,bool Complete=false,bool Cancelled=false,bool Finished=false)
{
    public double? Progress { get; init; }
    public double? EstimatedSecondsRemaining { get; init; }
    public long CapturedFrames { get; init; }
    public long RepeatedFrames { get; init; }
    public double? VideoFramesPerSecond { get; init; }
    public bool AudioCached { get; init; }
}

internal sealed class BackgroundRecording : IDisposable
{
    private sealed class Pass(bool audio,string file)
    {
        public readonly bool Audio=audio;
        public readonly string File=file;
        public Process? Process;
        public string State="loading",Error="";
        public bool Done,Stopped,ResultOk;
        public long Frames,Captured,Repeated,Dropped;
        public int TotalTicks;
        public JsonElement Recording;
        public readonly Stopwatch Clock=new();
        public long StartFrames;
        public double? Fps => Clock.Elapsed.TotalSeconds>=3 && Frames>StartFrames ? (Frames-StartFrames)/Clock.Elapsed.TotalSeconds : null;
    }
    private readonly string executable,film,mccRoot,sessionDirectory;
    private readonly string? settingsIni;
    private readonly int fov;
    private readonly bool isolatedAudioPass;
    private readonly int startTick;
    // Output video frame rate. 120 renders one interpolated frame between each pair of 60 Hz film ticks (Halo 3 only).
    private readonly int frameRate;
    private int FramesPerTick=>frameRate/60;
    private readonly double? recordSeconds;
    private readonly object commandGate=new(),logGate=new();
    private readonly FileStream reservation;
    private readonly CancellationTokenSource cancellation=new();
    private readonly TaskCompletionSource<BackgroundRecordingStatus> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Pass? audio,video;
    private bool cancelled,disposed,audioCached;
    private int startRequested;
    private string videoPayloadHash="";
    private BackgroundRecordingStatus status;
    public event Action<BackgroundRecordingStatus>? Changed;
    public Task<BackgroundRecordingStatus> Completion=>completion.Task;
    public bool IsRunning=>!completion.Task.IsCompleted&&!status.Finished;
    public BackgroundRecordingStatus Status {get {lock(commandGate)return status;}}

    private readonly ExportEncoding encoding;
    public BackgroundRecording(string executable,string film,string mccRoot,string sessionDirectory,double? recordSeconds=null,string? outputFile=null,string? settingsIni=null,int fov=91,int startTick=0,int frameRate=60,ExportEncoding? encoding=null)
    {
        this.executable=executable;this.film=film;this.mccRoot=mccRoot;this.sessionDirectory=sessionDirectory;
        this.recordSeconds=recordSeconds;this.settingsIni=settingsIni;this.fov=fov;this.startTick=startTick;this.frameRate=frameRate;
        this.encoding=encoding??new();this.encoding.Validate();
        var title=FilmTitle.Detect(film);
        isolatedAudioPass=title==FilmTitle.Halo4||title==FilmTitle.Halo2A;
        if(startTick<0||startTick>0&&(!recordSeconds.HasValue||recordSeconds<=0))throw new IOException("Ranged captures need a valid start tick and duration.");
        if(frameRate is not (60 or 120))throw new IOException("Choose 60 or 120 fps.");
        if(frameRate==120&&title!=FilmTitle.Halo3)throw new IOException("120 fps export is available for Halo 3 films only.");
        Directory.CreateDirectory(sessionDirectory);
        var selected=outputFile==null?ReserveOutput(film):ReserveExactOutput(outputFile);reservation=selected.Reservation;
        status=new("preparing",selected.File,"Preparing audio and video…");
    }
    public void Start(){if(Interlocked.Exchange(ref startRequested,1)==0)_=Run();}
    private double TargetFrames => CaptureProgress.TargetTicks(video?.TotalTicks is >0?video.TotalTicks:audio?.TotalTicks??0,recordSeconds);
    private void Update(string? phase=null,string? message=null)
    {
        lock(commandGate)
        {
            double target=TargetFrames;
            double Goal(Pass? p)=>p?.Audio==false?target*FramesPerTick:target; // video counts rendered frames, audio counts ticks
            double Fraction(Pass? p)=>p?.Done==true?1:target>0?Math.Clamp((p?.Frames??0)/Goal(p),0,.99):0;
            double? Eta(Pass? p)=>p?.Done==true?0:p?.Fps is double fps&&fps>0&&target>0?Math.Max(0,(Goal(p)-p.Frames)/fps):null;
            double? renderEta=Eta(video),audioEta=audioCached?0:Eta(audio);
            double? eta=isolatedAudioPass?(audio?.Done==true?renderEta:null):CaptureProgress.ParallelRemaining(renderEta,audioEta);
            var state=phase??(video?.State=="recording"||audio?.State=="recording"?"recording":"loading");
            var detail=message??(audio?.State=="warming"||video?.State=="warming"?"Advancing to the selected range without recording earlier footage":isolatedAudioPass&&audio?.Done!=true?"Capturing the game soundtrack before rendering video":audioCached?"Rendering video · cached audio":
                video?.Done==true?"Video ready · capturing audio":
                audio?.Done==true?"Audio ready · rendering video":"Capturing audio and rendering video together");
            status=new(state,status.File,detail,(video?.Frames??0)/(double)frameRate,video?.Frames??0,video?.Dropped??0,null,
                (int)Math.Max(0,(video?.Frames??0)/FramesPerTick-1),video?.TotalTicks??0)
            {
                Progress=target>0?Math.Min(.99,(Fraction(video)+(audioCached?1:Fraction(audio)))/2):null,
                EstimatedSecondsRemaining=state=="recording"?eta:null,
                VideoFramesPerSecond=video?.Fps,AudioCached=audioCached,
                CapturedFrames=video?.Captured??0,RepeatedFrames=video?.Repeated??0
            };
            Notify();
        }
    }
    private async Task Run()
    {
        string error="";
        string audioFile=Path.Combine(sessionDirectory,"audio-pass-"+Guid.NewGuid().ToString("N")+".mp4");
        string videoFile=Path.Combine(sessionDirectory,"video-pass-"+Guid.NewGuid().ToString("N")+".mp4");
        string muxFile=Path.Combine(Path.GetDirectoryName(status.File)!,"."+Path.GetFileNameWithoutExtension(status.File)+"-"+Guid.NewGuid().ToString("N")+".partial.mp4");
        string? cachePath=null;
        string syncedAudioFile=Path.Combine(sessionDirectory,"audio-synced-"+Guid.NewGuid().ToString("N")+".mp4");
        string soundtrack=audioFile;
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            if(!File.Exists(executable))throw new FileNotFoundException("The native capture backend has not been built.",executable);
            string ffmpeg=BatchRecording.FindTool("ffmpeg")??throw new IOException("Background export requires ffmpeg.exe for lossless stream copying.");
            if(BatchRecording.FindTool("ffprobe")==null)throw new IOException("Background export requires ffprobe.exe for validation.");
            string cache=AudioCaptureCache.PathFor(sessionDirectory,film,mccRoot,settingsIni,fov,recordSeconds,startTick,isolatedAudioPass);
            cachePath=cache;audioCached=AudioCaptureCache.Valid(cache);
            audio=new(true,audioCached?cache:audioFile){Done=audioCached};
            if(audioCached)soundtrack=cache;
            video=new(false,videoFile);
            if(audioCached)Log("background-process.jsonl",JsonSerializer.Serialize(new{type="audio-cache-reused",file=cache}));
            Update();
            var audioTask=audioCached?Task.CompletedTask:RunPass(audio,Path.Combine(sessionDirectory,"audio"));
            // Legacy Wwise titles advance sound events differently when another
            // engine competes for the GPU. Finish their normal-speed soundtrack
            // with exclusive rendering before starting the frame-stepped pass.
            if(isolatedAudioPass)await audioTask;
            var videoTask=RunPass(video,Path.Combine(sessionDirectory,"video"));
            await Task.WhenAll(audioTask,videoTask);
            cancellation.Token.ThrowIfCancellationRequested();
            if(!audioCached)
            {
                // The soundtrack is captured in real time while the video is rendered one frame per film tick.
                // The game's tick timer drifts against the PC clock, so place the soundtrack on film ticks.
                string ticks=audio.File+".ticks";
                if(!File.Exists(ticks))throw new IOException("The audio pass did not record film tick timing, so the soundtrack cannot be synchronized. Rebuild the native backend.");
                Update("finalizing","Synchronizing game audio to the film…");
                double drift=(video.Frames/(double)FramesPerTick-audio.Frames)/60.0;
                string resync;
                try{resync=await BatchRecording.Execute(executable,new[]{"--resync-audio",audio.File,"--ticks",ticks,"--output",syncedAudioFile,"--frames",video.Frames.ToString(System.Globalization.CultureInfo.InvariantCulture),"--fps",frameRate.ToString(System.Globalization.CultureInfo.InvariantCulture)},cancellation.Token);}
                catch(IOException e)when(!cancellation.IsCancellationRequested){
                    try{File.Copy(ticks,Path.Combine(sessionDirectory,"failed-audio.ticks"),true);}catch(Exception copy)when(copy is IOException or UnauthorizedAccessException){Log("shell-errors.log",copy.Message);} // Kept for diagnosis.
                    throw new IOException("Could not synchronize game audio to the film: "+e.Message.Replace(Path.GetFileName(executable)+" failed: ","").Trim());}
                soundtrack=syncedAudioFile;
                Log("background-process.jsonl",JsonSerializer.Serialize(new{type="audio-tick-synced",videoFrames=video.Frames,audioFrames=audio.Frames,driftSeconds=drift,result=resync.Trim()}));
                try{AudioCaptureCache.Store(soundtrack,cache);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Log("shell-errors.log","Audio cache: "+e.Message);}
            }
            Update("finalizing","Audio and video ready · combining without re-encoding…");
            var seconds=(video.Frames/(double)frameRate).ToString("R",System.Globalization.CultureInfo.InvariantCulture);
            videoPayloadHash=await VideoHash(ffmpeg,videoFile,cancellation.Token);
            // Each pass shares the selected source-frame origin. Keep timestamps
            // relative to that capture window;
            // copy compressed streams, including AAC priming information, without
            // time stretching, frame-rate conversion, or another lossy encode.
            await BatchRecording.Execute(ffmpeg,new[]{"-hide_banner","-v","error","-nostdin","-n",
                "-i",videoFile,"-i",soundtrack,"-map","0:v:0","-map","1:a:0","-c","copy",
                "-t",seconds,"-movie_timescale","48000","-movflags","+faststart",muxFile},cancellation.Token,Path.Combine(sessionDirectory,"mux.log"));
            var media=await BatchRecording.Probe(muxFile,cancellation.Token);
            if(media.Frames!=video.Frames)throw new IOException("Final MP4 frame count differs from the rendered video.");
            await ValidateMux(muxFile,video.Frames,frameRate,cancellation.Token);
            if(videoPayloadHash!=await VideoHash(ffmpeg,muxFile,cancellation.Token))throw new IOException("Final MP4 changed the encoded video payload.");
            await ValidateAudioCopy(soundtrack,muxFile,cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            File.Move(muxFile,status.File); // No overwrite, even if another application creates the destination.
            WriteFinalEvents();
            lock(commandGate)status=status with {State="completed",Message="Saved MP4: "+Path.GetFileName(status.File),Audio=true,Complete=true,Finished=true,Progress=1,EstimatedSecondsRemaining=0};
        }
        catch(Exception failure)
        {
            error=new[]{audio?.Error,video?.Error}.FirstOrDefault(e=>!string.IsNullOrEmpty(e)&&e!="The operation was canceled."&&e!="A task was canceled.")??failure.Message;
            // A cached soundtrack that failed final verification must not be reused by the next retry.
            if(audioCached&&!cancelled&&cachePath!=null&&video?.Done==true)
                foreach(string file in new[]{cachePath,cachePath+".sha256"})
                    try{if(File.Exists(file))File.Delete(file);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){Log("shell-errors.log","Audio cache: "+e.Message);}
            lock(commandGate)status=status with {State=cancelled?"cancelled":"error",Message=cancelled?"Background recording cancelled.":error,Complete=false,Cancelled=cancelled,Finished=true,EstimatedSecondsRemaining=null};
        }
        finally
        {
            foreach(string file in new[]{audioFile,audioFile+".ticks",syncedAudioFile,videoFile,muxFile})
                try{if(File.Exists(file))File.Delete(file);}catch(Exception failure)when(failure is IOException or UnauthorizedAccessException){Log("shell-errors.log",failure.Message);}
            Log("background-process.jsonl",JsonSerializer.Serialize(new{type="finished",cancelled,error,utc=DateTimeOffset.UtcNow}));
            Notify();try{reservation.Dispose();}finally{completion.TrySetResult(status);}
        }
    }
    private async Task RunPass(Pass pass,string directory)
    {
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=Path.GetDirectoryName(executable)!,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8};
        void Arg(string name,string value){info.ArgumentList.Add(name);info.ArgumentList.Add(value);}
        Arg("--background","true");Arg("--film",film);Arg("--title",FilmTitle.Detect(film).EngineFolder);Arg("--mcc-root",mccRoot);Arg("--output",directory);Arg("--record-output",pass.File);
        Arg(pass.Audio?"--audio-only":"--video-only","true");if(!pass.Audio&&frameRate!=60)Arg("--frame-rate",frameRate.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(!pass.Audio&&!encoding.CaptureIsDefault){Arg("--video-bitrate",encoding.CaptureBitrateMbps.ToString(System.Globalization.CultureInfo.InvariantCulture));Arg("--keyframe-half-seconds",encoding.KeyframeHalfSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture));}Arg("--fov",fov.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(settingsIni!=null)Arg("--settings-ini",settingsIni);
        if(recordSeconds is >0)Arg("--record-seconds",recordSeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(startTick>0)Arg("--record-start-tick",startTick.ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            Directory.CreateDirectory(directory);
            lock(commandGate)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                pass.Process=new Process{StartInfo=info};if(!pass.Process.Start())throw new IOException("Cannot start native export pass.");
            }
            Log("background-process.jsonl",JsonSerializer.Serialize(new{type="started",pass=pass.Audio?"audio":"video",processId=pass.Process.Id,executable,film,file=pass.File,utc=DateTimeOffset.UtcNow}));
            var stdout=ReadOutput(pass,pass.Process.StandardOutput);
            var stderr=DrainErrors(pass.Process.StandardError);
            var exited=pass.Process.WaitForExitAsync();
            // Cancellation/failure closes both children. A timeout bounds an engine
            // that stops responding; reader errors cannot leave a blocked pipe.
            var cancelledTask=Task.Delay(Timeout.Infinite,cancellation.Token);
            var readerFailure=ReaderFailure(Task.WhenAll(stdout,stderr));
            var first=await Task.WhenAny(exited,cancelledTask,readerFailure);
            if(first!=exited)
            {
                CommandAll();
                if(await Task.WhenAny(exited,Task.Delay(15000))!=exited)try{pass.Process.Kill(false);}catch(InvalidOperationException){}
            }
            await exited;await Task.WhenAll(stdout,stderr);
            cancellation.Token.ThrowIfCancellationRequested();
            if(pass.Process.ExitCode!=0||!pass.ResultOk||!pass.Stopped||pass.Error.Length>0||pass.Frames<=0||pass.Dropped!=0||
                (!pass.Audio&&(pass.Captured!=pass.Frames||pass.Repeated!=0))||!File.Exists(pass.File))
                throw new IOException(pass.Error.Length>0?pass.Error:$"The {(pass.Audio?"audio":"video")} pass did not validate successfully.");
            lock(commandGate){pass.Done=true;Update();}
        }
        catch(Exception e)
        {
            lock(commandGate){pass.Error=e.Message;cancellation.Cancel();CommandAll();}
            throw;
        }
        finally{lock(commandGate){pass.Process?.Dispose();pass.Process=null;}}
    }
    private async Task ReadOutput(Pass pass,StreamReader reader)
    {
        string? line;
        while((line=await reader.ReadLineAsync())!=null)
        {
            if(line.Length>65536)throw new IOException("Native export returned an oversized event.");
            Log(pass.Audio?"audio-pass-events.jsonl":"video-pass-events.jsonl",line);
            using var document=JsonDocument.Parse(line);var value=document.RootElement;
            // Keep effective render/visibility telemetry available in the public
            // event log; pass-specific output and intermediate results stay separate.
            if(!pass.Audio&&value.TryGetProperty("type",out var eventType)&&eventType.GetString() is "telemetry" or "window")
                Log("backend-events.jsonl",line);
            string S(string key)=>value.TryGetProperty(key,out var p)&&p.ValueKind==JsonValueKind.String?p.GetString()??"":"";
            long N(string key)=>value.TryGetProperty(key,out var p)&&p.TryGetInt64(out long n)?n:0;
            lock(commandGate)
            {
                switch(S("type"))
                {
                    case "telemetry":pass.TotalTicks=(int)N("totalTicks");break;
                    case "error":pass.Error=S("message");break;
                    case "status":if(S("phase")=="warming")pass.State="warming";break;
                    case "backgroundResult":
                        pass.ResultOk=value.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.True&&N("initialFilmTick")==startTick;
                        pass.TotalTicks=(int)N("totalTicks");break;
                    case "recording":
                        pass.State=S("state");pass.Frames=N("frames");pass.Captured=N("capturedFrames");pass.Repeated=N("repeatedFrames");pass.Dropped=N("droppedFrames");
                        if(pass.State=="recording"&&!pass.Clock.IsRunning){pass.StartFrames=pass.Frames;pass.Clock.Start();}
                        if(pass.State=="stopped"){pass.Stopped=true;pass.Clock.Stop();pass.Recording=value.Clone();}
                        if(pass.State=="error")pass.Error=S("error");
                        break;
                }
                Update();
            }
        }
    }
    private static async Task ValidateMux(string file,long frames,int frameRate,CancellationToken token)
    {
        var json=await BatchRecording.Execute(BatchRecording.FindTool("ffprobe")!,new[]{"-v","error","-show_streams","-of","json",file},token);
        using var document=JsonDocument.Parse(json);
        var streams=document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.Single(s=>s.GetProperty("codec_type").GetString()=="video");
        var audio=streams.Single(s=>s.GetProperty("codec_type").GetString()=="audio");
        double Number(JsonElement s,string key)=>double.Parse(s.GetProperty(key).ToString(),System.Globalization.CultureInfo.InvariantCulture);
        if(Number(video,"width")!=2560||Number(video,"height")!=1440||video.GetProperty("r_frame_rate").GetString()!=$"{frameRate}/1"||
           video.GetProperty("profile").GetString()!="High"||Number(audio,"sample_rate")!=48000||Number(audio,"channels")!=2||
           Math.Abs(Number(video,"start_time"))>1.0/48000||Math.Abs(Number(audio,"start_time"))>1.0/48000||
           Math.Abs(Number(video,"duration")-frames/(double)frameRate)>.001||Math.Abs(Number(audio,"duration")-frames/(double)frameRate)>.025)
            throw new IOException("Final MP4 format or audio/video duration verification failed.");
    }
    private static Task<string> VideoHash(string ffmpeg,string file,CancellationToken token)=>
        BatchRecording.Execute(ffmpeg,new[]{"-v","error","-nostdin","-i",file,"-map","0:v:0","-c","copy","-f","hash","-hash","sha256","-"},token);
    private static async Task ValidateAudioCopy(string source,string output,CancellationToken token)
    {
        async Task<JsonDocument> Packets(string path)=>JsonDocument.Parse(await BatchRecording.Execute(BatchRecording.FindTool("ffprobe")!,new[]{"-v","error","-select_streams","a:0","-show_packets","-show_data_hash","sha256","-show_entries","packet=pts_time,data_hash","-of","json",path},token));
        using var input=await Packets(source);using var final=await Packets(output);
        var before=input.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        var after=final.RootElement.GetProperty("packets").EnumerateArray().ToArray();
        if(after.Length==0||after.Length>before.Length)throw new IOException("Final MP4 audio packet count is invalid.");
        for(int i=0;i<after.Length;i++)
        {
            double Time(JsonElement packet)=>double.Parse(packet.GetProperty("pts_time").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
            if(after[i].GetProperty("data_hash").GetString()!=before[i].GetProperty("data_hash").GetString()||Math.Abs(Time(after[i])-Time(before[i]))>1.0/48000)
                throw new IOException("Final MP4 changed an audio packet or shifted its sample timing.");
        }
    }
    private void WriteFinalEvents()
    {
        var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(video!.Recording.GetRawText())!;
        void Set(string name,object? value)=>fields[name]=JsonSerializer.SerializeToElement(value);
        Set("file",status.File);Set("path",status.File);Set("videoOnly",false);Set("audio",true);
        Set("audioFrames",video.Frames*(48000/frameRate));Set("frameRate",frameRate);Set("audioSourceFrames",null);
        Set("audioGapFrames",null);Set("audioDiscontinuities",null);Set("audioNonSilentFrames",null);
        Set("audioSource","xaudio2-mastering-mix");Set("audioCached",audioCached);Set("parallelPasses",!audioCached&&!isolatedAudioPass);Set("isolatedAudioPass",isolatedAudioPass);
        Set("audioPacketCopied",true);Set("audioTimingProvenance",audioCached?"cached soundtrack; upstream gap counts unavailable":"native audio pass");
        Set("videoPayloadSha256",videoPayloadHash.Trim());Set("audioPacketTimingVerified",true);
        if(!audioCached)
            foreach(string key in new[]{"audioSourceFrames","audioGapFrames","audioDiscontinuities","audioNonSilentFrames"})
                if(audio!.Recording.TryGetProperty(key,out var p))fields[key]=p;
        Log("backend-events.jsonl",JsonSerializer.Serialize(fields));
        Log("backend-events.jsonl",JsonSerializer.Serialize(new{type="backgroundResult",ok=true,file=status.File,initialFilmTick=0,lastFilmTick=video.Frames/FramesPerTick-1,totalTicks=video.TotalTicks,windowVisible=false}));
    }
    private async Task DrainErrors(StreamReader reader){string? line;while((line=await reader.ReadLineAsync())!=null)Log("backend-stderr.log",line);}
    private void Notify(){var handlers=Changed;if(handlers==null)return;foreach(Action<BackgroundRecordingStatus> callback in handlers.GetInvocationList())try{callback(status);}catch(Exception e){Log("shell-errors.log",e.Message);}}
    private void Log(string name,string line){try{lock(logGate)File.AppendAllText(Path.Combine(sessionDirectory,name),line+Environment.NewLine);}catch(Exception e)when(e is IOException or UnauthorizedAccessException){}}
    private void CommandAll()
    {
        lock(commandGate)foreach(var pass in new[]{audio,video})
            try{if(pass?.Process is {HasExited:false} process){process.StandardInput.WriteLine("{\"command\":\"close\"}");process.StandardInput.Flush();}}
            catch(Exception e)when(e is IOException or InvalidOperationException){Log("shell-errors.log",e.Message);}
    }
    public async Task CancelAsync(){if(!IsRunning)return;lock(commandGate){cancelled=true;cancellation.Cancel();CommandAll();}if(Volatile.Read(ref startRequested)==0)Start();await completion.Task;}
    public void OpenFolder(){Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={Path.GetDirectoryName(status.File)!}});}
    public void Dispose(){if(disposed)return;disposed=true;if(!IsRunning){reservation.Dispose();cancellation.Dispose();}}
    private static async Task<Exception> ReaderFailure(Task reader)
    {
        try{await reader;}catch(Exception failure){return failure;}
        return await new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
    private static (string File,FileStream Reservation) ReserveOutput(string film)
    {
        string folder=DownloadsFolder();Directory.CreateDirectory(folder);string stem=Path.GetFileNameWithoutExtension(film);
        foreach(char character in Path.GetInvalidFileNameChars())stem=stem.Replace(character,'_');stem=stem.Trim().TrimEnd('.');if(stem.Length==0)stem="Halo3 recording";
        for(int number=0;number<10000;number++)
        {
            string file=Path.Combine(folder,stem+(number==0?"":" ("+number+")")+".mp4");if(File.Exists(file))continue;
            try
            {
                var owned=new FileStream(file+".nativecapture-reservation",FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
                if(File.Exists(file)){owned.Dispose();continue;}return(file,owned);
            }
            catch(IOException){}
        }
        throw new IOException("Unable to reserve a new MP4 filename in Downloads.");
    }
    private static (string File,FileStream Reservation) ReserveExactOutput(string output)
    {
        string file=Path.GetFullPath(output);if(!file.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose an .mp4 output file.");
        if(File.Exists(file))throw new IOException("The requested MP4 already exists; background capture never overwrites a video.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var owned=new FileStream(file+".nativecapture-reservation",FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
        if(File.Exists(file)){owned.Dispose();throw new IOException("The requested MP4 was created by another process; it was kept.");}
        return(file,owned);
    }
    internal static string DownloadsFolder()
    {
        Guid id=new("374DE290-123F-4565-9164-39C4925E467B");IntPtr path=IntPtr.Zero;
        try{if(SHGetKnownFolderPath(ref id,0,IntPtr.Zero,out path)==0&&path!=IntPtr.Zero)return Marshal.PtrToStringUni(path)!;}finally{if(path!=IntPtr.Zero)Marshal.FreeCoTaskMem(path);}
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads");
    }
    [DllImport("shell32.dll")]private static extern int SHGetKnownFolderPath(ref Guid id,uint flags,IntPtr token,out IntPtr path);
}

internal static class BackgroundRecordingCli
{
    private static string? Argument(string[] args,string name){int index=Array.IndexOf(args,name);return index>=0&&index+1<args.Length?args[index+1]:null;}
    public static bool ShouldRun(string[] args)=>bool.TryParse(Argument(args,"--background"),out var background)&&background&&Argument(args,"--film")!=null;
    public static async Task Run(string[] args)
    {
        string dataRoot=Path.GetFullPath(Argument(args,"--data-dir")??Path.Combine(AppContext.BaseDirectory,"UserData"));
        string nativeHost=Path.GetFullPath(Argument(args,"--native-host")??Path.Combine(AppContext.BaseDirectory,"native","NativeReplayBackend.exe"));
        string film=Path.GetFullPath(Argument(args,"--film")!);var events=new List<object>();var gate=new object();var clock=Stopwatch.StartNew();
        BackgroundRecordingStatus? result=null;
        try
        {
            Directory.CreateDirectory(dataRoot);
            if(!File.Exists(film)||!film.EndsWith(".mov",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose an original MCC .mov recording.");
            string? mccRoot=Argument(args,"--mcc-root");
            if(mccRoot==null)
            {
                string? maps=Argument(args,"--maps");
                if(maps==null)
                {
                    string settings=Path.Combine(dataRoot,"settings.json");
                    if(File.Exists(settings))
                    {
                        try{using var preferences=JsonDocument.Parse(File.ReadAllText(settings));if(preferences.RootElement.TryGetProperty("maps",out var value)&&value.ValueKind==JsonValueKind.String&&Directory.Exists(value.GetString()))maps=value.GetString();}catch(JsonException){}
                    }
                }
                maps??=ViewerWindow.FindMaps();mccRoot=maps!=null?FilmTitle.InstallationRoot(maps):null;
            }
            if(mccRoot==null)throw new IOException("Choose the MCC installation with --mcc-root before background recording.");
            mccRoot=FilmTitle.InstallationRoot(mccRoot);
            var filmTitle=FilmTitle.Detect(film);
            if(Argument(args,"--title") is string requestedTitle&&FilmTitle.FromId(requestedTitle)!=filmTitle)throw new IOException("The requested title does not match the film header.");
            if(filmTitle.InstallationIssue(mccRoot) is string installationIssue)throw new IOException(installationIssue);
            double? seconds=null;
            if(Array.IndexOf(args,"--record-seconds")>=0)
            {
                string? duration=Argument(args,"--record-seconds");
                if(!double.TryParse(duration,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var requested)||!double.IsFinite(requested)||requested<=0||requested>43200)
                    throw new IOException("--record-seconds must be a positive finite duration no greater than 43200 seconds. Omit it to record the full film.");
                seconds=requested;
            }
            var session=Path.Combine(dataRoot,"background",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            int savedFov=91;
            var preferencesPath=Path.Combine(dataRoot,"settings.json");
            if(File.Exists(preferencesPath))
            {
                try{savedFov=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(preferencesPath),new JsonSerializerOptions{PropertyNameCaseInsensitive=true})?.FovDegrees??91;}catch(JsonException){}
            }
            int startTick=0;
            if(Argument(args,"--record-start-tick") is string start&&(!int.TryParse(start,System.Globalization.NumberStyles.None,System.Globalization.CultureInfo.InvariantCulture,out startTick)||startTick<0))
                throw new IOException("--record-start-tick must be a non-negative film tick.");
            using var job=new BackgroundRecording(nativeHost,film,mccRoot,session,seconds,Argument(args,"--record-output"),Program.SettingsIniPath(args),Program.FovDegrees(args,savedFov),startTick,frameRate:int.TryParse(Argument(args,"--frame-rate"),out var fps)?fps:60,
                encoding:new ExportEncoding{CaptureBitrateMbps=int.TryParse(Argument(args,"--video-bitrate"),out var mbps)?mbps:50,
                    KeyframeSeconds=double.TryParse(Argument(args,"--keyframe-seconds"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var gop)?gop:.5});
            job.Changed+=value=>{lock(gate)events.Add(new{type="background-recording",elapsedSeconds=clock.Elapsed.TotalSeconds,utc=DateTimeOffset.UtcNow,detail=value});};
            job.Start();
            if(double.TryParse(Argument(args,"--auto-close-seconds"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var watchdog)&&watchdog>0&&await Task.WhenAny(job.Completion,Task.Delay(TimeSpan.FromSeconds(watchdog)))!=job.Completion)await job.CancelAsync();
            result=await job.Completion;
        }
        catch(Exception error){result=new("error","",error.Message,Finished:true);}
        finally
        {
            Environment.ExitCode=result?.Complete==true?0:1;
            if(Argument(args,"--report") is string report)
            {
                try
                {
                    report=Path.GetFullPath(report);Directory.CreateDirectory(Path.GetDirectoryName(report)!);
                    object[] snapshot;lock(gate)snapshot=events.ToArray();
                    File.WriteAllText(report,JsonSerializer.Serialize(new{executable=Environment.ProcessPath,noViewerWindow=true,dataRoot,nativeHost,film,elapsedSeconds=clock.Elapsed.TotalSeconds,backgroundRecording=result,
                        capture=new{state=result?.Complete==true?"stopped":result?.State,file=result?.File,durationSeconds=result?.DurationSeconds,frames=result?.Frames,capturedFrames=result?.CapturedFrames,repeatedFrames=result?.RepeatedFrames,droppedFrames=result?.DroppedFrames,audio=result?.Audio,complete=result?.Complete},events=snapshot},new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true}));
                }
                catch(IOException){Environment.ExitCode=1;}
            }
        }
    }
}
