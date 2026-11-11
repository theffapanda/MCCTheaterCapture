using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal sealed class BatchItem
{
    public string Film { get; set; } = "";
    public string Name { get; set; } = "";
    public string Map { get; set; } = "";
    public string Title { get; set; } = "halo3";
    public int TotalTicks { get; set; }
    public long SourceBytes { get; set; }
    public string SourceModified { get; set; } = "";
    public string State { get; set; } = "queued";
    public string File { get; set; } = "";
    public string Error { get; set; } = "";
    public long Frames { get; set; }
    public string Sha256 { get; set; } = "";
    public double DurationSeconds { get; set; }
}

// User-adjustable encoder levers. Defaults reproduce the original fixed profile,
// so jobs and caches created before these settings existed keep their signatures.
internal sealed class ExportEncoding
{
    internal static readonly int[] CaptureBitrates = { 25, 35, 50, 80, 100, 150 };
    internal static readonly double[] KeyframeIntervals = { .5, 1, 2 };
    internal static readonly string[] Presets = { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" };
    internal static readonly string[] Tunes = { "none", "film", "animation", "grain" };
    internal static readonly int[] AudioBitrates = { 128, 192, 256, 320 };
    // Native Media Foundation H.264 target at 60 fps (doubled for 120 fps).
    public int CaptureBitrateMbps { get; set; } = 50;
    // Native capture keyframe spacing in seconds.
    public double KeyframeSeconds { get; set; } = .5;
    // x264 CRF used when quality is "custom" (0 = lossless, 51 = worst).
    public int Crf { get; set; } = 18;
    public string Preset { get; set; } = "fast";
    public string Tune { get; set; } = "none";
    // AAC bitrate for re-encoded highlight clip audio.
    public int AudioBitrateKbps { get; set; } = 192;

    public void Validate()
    {
        if (!CaptureBitrates.Contains(CaptureBitrateMbps)) throw new IOException("Choose a capture bitrate of " + string.Join(", ", CaptureBitrates) + " Mbps.");
        if (!KeyframeIntervals.Contains(KeyframeSeconds)) throw new IOException("Choose a keyframe interval of 0.5, 1 or 2 seconds.");
        if (Crf is < 0 or > 51) throw new IOException("Custom CRF must be between 0 and 51.");
        if (!Presets.Contains(Preset)) throw new IOException("Choose an x264 encoder speed preset.");
        if (!Tunes.Contains(Tune)) throw new IOException("Choose an x264 tune.");
        if (!AudioBitrates.Contains(AudioBitrateKbps)) throw new IOException("Choose an audio bitrate of " + string.Join(", ", AudioBitrates) + " kbps.");
    }
    public bool CaptureIsDefault => CaptureBitrateMbps == 50 && KeyframeSeconds == .5;
    public int KeyframeHalfSeconds => (int)Math.Round(KeyframeSeconds * 2);
    // Appended to signatures and cache keys only when non-default.
    public string CaptureKey => CaptureIsDefault ? "" : $"|capture:{CaptureBitrateMbps}mbps:gop{KeyframeHalfSeconds}";
    public string ExportKey(string quality, bool audio = false) =>
        (quality == "custom" ? $"|crf:{Crf}" : "") + (Preset != "fast" ? "|preset:" + Preset : "") + (Tune != "none" ? "|tune:" + Tune : "") + (audio && AudioBitrateKbps != 192 ? $"|aac:{AudioBitrateKbps}" : "");
    public int CrfFor(string quality) => quality switch { "maximum" => 12, "high" => 16, "standard" => 20, "custom" => Crf, _ => throw new IOException("Unsupported quality.") };
    public string[] X264Arguments(string quality)
    {
        var args = new List<string> { "-c:v", "libx264", "-preset", Preset, "-crf", CrfFor(quality).ToString(CultureInfo.InvariantCulture) };
        if (Tune != "none") args.AddRange(new[] { "-tune", Tune });
        // CRF 0 is lossless, which x264 encodes as High 4:4:4 Predictive (forcing -profile high would fail).
        // Such files are huge and some players/editors reject them.
        args.AddRange(CrfFor(quality) == 0 ? new[] { "-pix_fmt", "yuv420p" } : new[] { "-profile:v", "high", "-pix_fmt", "yuv420p" });
        return args.ToArray();
    }
}

internal sealed class BatchPlan
{
    public string Schema { get; set; } = "mcc-batch/v1";
    public string CaptureProfile { get; set; } = "offline-1440p60-v2";
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Theater reel";
    public string Mode { get; set; } = "separate";
    public string OutputFormat { get; set; } = "mp4";
    public int OutputResolution { get; set; } = 1440;
    public string OutputQuality { get; set; } = "high";
    public ExportEncoding Encoding { get; set; } = new();
    public string MccRoot { get; set; } = "";
    public string OutputDirectory { get; set; } = "";
    public string? SettingsIni { get; set; }
    public string SettingsSnapshot { get; set; } = "";
    public int Fov { get; set; } = 91;
    public int FrameRate { get; set; } = 60;
    public string CaptureKind { get; set; } = "full";
    public HighlightOptions? HighlightOptions { get; set; }
    public HighlightFormat HighlightFormat { get; set; } = new();
    public Dictionary<string, HighlightCaption> HighlightCaptions { get; set; } = new();
    public double? SampleSeconds { get; set; }
    public bool ContinueOnError { get; set; } = true;
    public List<BatchItem> Items { get; set; } = new();
    public string State { get; set; } = "draft";
    public string Message { get; set; } = "Ready to build your queue.";
    public string CombinedFile { get; set; } = "";
    public string CombinedSha256 { get; set; } = "";
    public string Signature { get; set; } = "";
    public string Checkpoint { get; set; } = "";
    public int ActiveIndex { get; set; } = -1;
    public double ActiveProgress { get; set; }
    public double Progress { get; set; }
    public double? EstimatedSecondsRemaining { get; set; }
    public double? CurrentFilmSecondsRemaining { get; set; }
    public bool Finished { get; set; }
}

internal sealed class BatchRecording : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive=true, PropertyNamingPolicy=JsonNamingPolicy.CamelCase, WriteIndented=true };
    private readonly string executable, checkpoint;
    private readonly CancellationTokenSource cancellation = new();
    private BackgroundRecording? active;
    private Task? running;
    private long lastProgress;
    public BatchPlan Plan { get; }
    public bool IsRunning => running is { IsCompleted:false };
    public event Action<BatchPlan>? Changed;
    public Task Completion => running??Task.CompletedTask;

    public BatchRecording(BatchPlan plan,string executable,string checkpoint)
    {
        // Freeze the submitted plan; editing a draft cannot change an active job.
        Plan=JsonSerializer.Deserialize<BatchPlan>(JsonSerializer.Serialize(plan,Json),Json)!;
        this.executable=executable;this.checkpoint=Path.GetFullPath(checkpoint);
        Plan.Checkpoint=this.checkpoint;
    }

    internal static string? FindTool(string name)
    {
        var candidates=new List<string>{Path.Combine(AppContext.BaseDirectory,"tools",name+".exe")};
        foreach(var folder in (Environment.GetEnvironmentVariable("PATH")??"").Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(folder.Trim('"'),name+".exe"));
        return candidates.FirstOrDefault(File.Exists);
    }

    internal static void Validate(BatchPlan plan)
    {
        ValidateOutputFormat(plan.OutputFormat);
        ValidateVideoProfile(plan.OutputResolution,plan.OutputQuality);
        plan.Encoding??=new();plan.Encoding.Validate();
        if(plan.CaptureKind=="highlights")throw new IOException("Multikill exports require a mcc-highlights/v1 job. Find and select highlights in the export desk first.");
        if(plan.Schema!="mcc-batch/v1"||!Guid.TryParseExact(plan.Id,"N",out _))throw new IOException("Unsupported batch job or invalid job ID.");
        if(plan.CaptureProfile!="offline-1440p60-v2")throw new IOException("This job uses an older capture profile. Start a New reel to render every film frame at 60 fps.");
        if(plan.Mode is not ("separate" or "combined" or "both"))throw new IOException("Choose Separate MP4s, One combined MP4, or Both.");
        if(plan.Fov<55||plan.Fov>120)throw new IOException("Batch FOV must be between 55 and 120 degrees.");
        if(plan.FrameRate is not (60 or 120))throw new IOException("Choose 60 or 120 fps.");
        if(plan.SampleSeconds.HasValue&&(!double.IsFinite(plan.SampleSeconds.Value)||plan.SampleSeconds<=0||plan.SampleSeconds>43200))throw new IOException("Sample duration must be between 0 and 43200 seconds, or null for full films.");
        if(plan.Items==null||plan.Items.Count==0||plan.Items.Count>500)throw new IOException("Choose between 1 and 500 films for this job.");
        if(string.IsNullOrWhiteSpace(plan.MccRoot))throw new IOException("Choose the installed MCC content folder before starting a batch.");
        plan.MccRoot=FilmTitle.InstallationRoot(plan.MccRoot);
        if(string.IsNullOrWhiteSpace(plan.OutputDirectory))throw new IOException("Choose an output folder.");
        plan.OutputDirectory=Path.GetFullPath(plan.OutputDirectory);
        if(plan.OutputDirectory.Contains('\n')||plan.OutputDirectory.Contains('\r'))throw new IOException("Output folder contains an invalid line break.");
        if(plan.SettingsIni!=null){plan.SettingsIni=Path.GetFullPath(plan.SettingsIni);if(plan.SettingsSnapshot.Length==0&&!File.Exists(plan.SettingsIni))throw new IOException("The selected MCC settings INI does not exist.");}
        foreach(var item in plan.Items)
        {
            var film=ViewerWindow.ReadFilm(item.Film,plan.MccRoot);
            if(!film.CanDecode)throw new IOException(film.Reason);
            if(item.SourceBytes!=0&&(item.SourceBytes!=film.Bytes||item.SourceModified!=film.Modified))throw new IOException($"Source film changed: {film.Name}. Create a new job to capture it.");
            item.Film=film.Path;item.Name=film.Name;item.Map=film.Map;item.Title=film.Title;item.TotalTicks=film.TotalTicks;item.SourceBytes=film.Bytes;item.SourceModified=film.Modified;
        }
        if(plan.FrameRate==120&&plan.Items.Any(i=>FilmTitle.Detect(i.Film)!=FilmTitle.Halo3))throw new IOException("120 fps export is available for Halo 3 films only. Remove the other films or choose 60 fps.");
        if(FindTool("ffmpeg")==null||FindTool("ffprobe")==null)throw new IOException("Exports need ffmpeg.exe and ffprobe.exe on PATH or in the app's tools folder to combine audio and video without re-encoding.");
        var signatureData=JsonSerializer.Serialize(new{renderRevision="native-medals-v1",plan.CaptureProfile,plan.Mode,plan.Fov,plan.SampleSeconds,plan.MccRoot,plan.OutputDirectory,plan.SettingsIni,settingsHash=plan.SettingsSnapshot.Length>0?Hash(plan.SettingsSnapshot):plan.SettingsIni!=null?Hash(plan.SettingsIni):HashDefaultSettings(),films=plan.Items.Select(i=>new{i.Film,i.SourceBytes,i.SourceModified})});
        if(plan.Items.Select(i=>i.Film).Distinct(StringComparer.OrdinalIgnoreCase).Any(film=>{var title=FilmTitle.Detect(film);return title==FilmTitle.Halo4||title==FilmTitle.Halo2A;}))signatureData+="|isolated-legacy-audio-qpc-v2";
        if(plan.Items.Select(i=>i.Film).Distinct(StringComparer.OrdinalIgnoreCase).Any(film=>{var title=FilmTitle.Detect(film);return title==FilmTitle.Halo4||title==FilmTitle.Halo2A;}))signatureData+="|native-h4-h2a-hud-fonts-v1";
        if(plan.OutputFormat!="mp4")signatureData+="|container:"+plan.OutputFormat;
        if(plan.FrameRate!=60)signatureData+="|fps:"+plan.FrameRate;
        if(plan.OutputResolution!=1440||plan.OutputQuality!="high")signatureData+=$"|video:{plan.OutputResolution}:{plan.OutputQuality}"+plan.Encoding.ExportKey(plan.OutputQuality);
        signatureData+=plan.Encoding.CaptureKey;
        var signature=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signatureData)));
        if(plan.Signature.Length>0&&plan.Signature!=signature)throw new IOException("This job's sources, capture settings or renderer changed. Start a New reel to avoid reusing incompatible captures.");
        plan.Signature=signature;
    }

    private static string HashDefaultSettings()
    {
        var file=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow","MCC","Saved","Config","WindowsNoEditor","GameUserSettings.ini");
        return File.Exists(file)?Hash(file):Convert.ToHexString(SHA256.HashData(Array.Empty<byte>()));
    }
    internal static string Hash(string path){using var file=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(file));}
    internal static string SafeName(string name)
    {
        var cleaned=new string(name.Select(c=>Path.GetInvalidFileNameChars().Contains(c)||char.IsControl(c)?'_':c).ToArray()).Trim().TrimEnd('.');
        if(cleaned.Length>72)cleaned=cleaned[..72].TrimEnd('.');
        var stem=cleaned.Split('.')[0];
        if(new[]{"CON","PRN","AUX","NUL","COM1","COM2","COM3","COM4","COM5","COM6","COM7","COM8","COM9","LPT1","LPT2","LPT3","LPT4","LPT5","LPT6","LPT7","LPT8","LPT9"}.Contains(stem,StringComparer.OrdinalIgnoreCase))cleaned="_"+cleaned;
        return string.IsNullOrWhiteSpace(cleaned)?"Theater reel":cleaned;
    }
    internal static BatchPlan Read(string path)=>JsonSerializer.Deserialize<BatchPlan>(File.ReadAllText(path),Json)??throw new IOException("Empty batch job.");
    internal static void Save(string path,BatchPlan plan)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        File.WriteAllText(temp,JsonSerializer.Serialize(plan,Json));File.Move(temp,path,true);
    }
    public void Start(){if(running!=null)throw new InvalidOperationException("Batch already started.");running=Task.Run(Run);}
    private void Publish(string state,string message,bool save=true)
    {
        double Target(BatchItem item)=>CaptureProgress.TargetTicks(item.TotalTicks,Plan.SampleSeconds);
        double total=Plan.Items.Sum(Target);
        double completed=Plan.Items.Where(i=>i.State=="completed").Sum(Target);
        double current=Plan.ActiveIndex>=0&&Plan.ActiveIndex<Plan.Items.Count&&Plan.Items[Plan.ActiveIndex].State!="completed"?Target(Plan.Items[Plan.ActiveIndex])*Plan.ActiveProgress:0;
        Plan.Progress=state=="completed"?1:total>0?Math.Min(.99,(completed+current)/total):0;
        if(state!="capturing"){Plan.EstimatedSecondsRemaining=state=="completed"?0:null;Plan.CurrentFilmSecondsRemaining=state=="completed"?0:null;}
        Plan.State=state;Plan.Message=message;if(save)Save(checkpoint,Plan);
        Changed?.Invoke(Plan);
    }
    private async Task Run()
    {
        FileStream? jobLock=null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(checkpoint)!);
            jobLock=new FileStream(checkpoint+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None,1,FileOptions.DeleteOnClose);
            Validate(Plan);
            if(!File.Exists(executable))throw new FileNotFoundException("Native capture backend is missing.",executable);
            Directory.CreateDirectory(Plan.OutputDirectory);
            // Probe write access before launching any renderer.
            var probe=Path.Combine(Plan.OutputDirectory,".write-"+Guid.NewGuid().ToString("N"));
            using(var write=new FileStream(probe,FileMode.CreateNew,FileAccess.Write,FileShare.None,1,FileOptions.DeleteOnClose)){}
            if(Plan.SettingsSnapshot.Length==0)
            {
                var original=Plan.SettingsIni??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow","MCC","Saved","Config","WindowsNoEditor","GameUserSettings.ini");
                Plan.SettingsSnapshot=Path.Combine(Path.GetDirectoryName(checkpoint)!,"view-settings-"+Guid.NewGuid().ToString("N")+".ini");
                if(File.Exists(original))File.Copy(original,Plan.SettingsSnapshot,false);else File.WriteAllBytes(Plan.SettingsSnapshot,Array.Empty<byte>());
                Validate(Plan); // The snapshot must match the preflighted source.
            }
            Plan.Finished=false;Plan.EstimatedSecondsRemaining=null;Publish("preparing","Checking completed clips and preparing the queue\u2026");
            var audioCached=new bool[Plan.Items.Count];
            var isolatedAudioPass=new bool[Plan.Items.Count];
            for(int i=0;i<Plan.Items.Count;i++)
            {
                try{var title=FilmTitle.Detect(Plan.Items[i].Film);isolatedAudioPass[i]=title==FilmTitle.Halo4||title==FilmTitle.Halo2A;
                    audioCached[i]=AudioCaptureCache.Valid(AudioCaptureCache.PathFor(
                    Path.Combine(Path.GetDirectoryName(checkpoint)!,"sessions","estimate"),Plan.Items[i].Film,Plan.MccRoot,Plan.SettingsSnapshot,Plan.Fov,Plan.SampleSeconds,isolatedAudioPass:isolatedAudioPass[i]));}
                catch(IOException){audioCached[i]=false;}
            }
            for(int index=0;index<Plan.Items.Count;index++)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                Plan.ActiveIndex=index;Plan.ActiveProgress=0;Plan.EstimatedSecondsRemaining=null;Plan.CurrentFilmSecondsRemaining=null;var item=Plan.Items[index];
                if(item.State=="completed"&&File.Exists(item.File)&&item.Sha256.Length>0&&Hash(item.File)==item.Sha256)continue;
                item.State="queued";item.Error="";
                // Every attempt has a fresh filename; partial and existing files are preserved.
                var clipFolder=Plan.Mode=="combined"?Path.Combine(Plan.OutputDirectory,".parts"):Plan.OutputDirectory;
                var captureFolder=Plan.OutputFormat=="mp4"&&Plan.OutputResolution==1440&&Plan.OutputQuality=="high"?clipFolder:Path.Combine(Plan.OutputDirectory,".parts");
                Directory.CreateDirectory(captureFolder);
                var file=Path.Combine(captureFolder,$"{index+1:000}-{SafeName(Path.GetFileNameWithoutExtension(item.Name))}-{Guid.NewGuid().ToString("N")[..6]}.mp4");
                var session=Path.Combine(Path.GetDirectoryName(checkpoint)!,"sessions",$"{index+1:000}-{Guid.NewGuid():N}");
                try
                {
                    // Check source identity again immediately before this film starts.
                    var info=new FileInfo(item.Film);
                    if(info.Length!=item.SourceBytes||info.LastWriteTimeUtc.ToString("O")!=item.SourceModified)throw new IOException("The source film changed after this job started.");
                    item.State="loading";item.File=file;Publish("capturing",$"Loading {index+1} of {Plan.Items.Count}: {item.Name}");
                    using var job=new BackgroundRecording(executable,item.Film,Plan.MccRoot,session,Plan.SampleSeconds,file,Plan.SettingsSnapshot,Plan.Fov,frameRate:Plan.FrameRate,encoding:Plan.Encoding);
                    active=job;
                    job.Changed+=status=>
                    {
                        item.State=status.State;item.Frames=status.Frames;item.DurationSeconds=status.DurationSeconds;
                        Plan.ActiveProgress=status.Progress??0;
                        Plan.CurrentFilmSecondsRemaining=status.EstimatedSecondsRemaining;
                        double future=0;
                        for(int next=index+1;next<Plan.Items.Count;next++)if(Plan.Items[next].State!="completed")
                            future+=CaptureProgress.FilmWork(CaptureProgress.TargetTicks(Plan.Items[next].TotalTicks,Plan.SampleSeconds),status.VideoFramesPerSecond/(Plan.FrameRate/60),audioCached[next],isolatedAudioPass[next])??double.NaN;
                        Plan.EstimatedSecondsRemaining=status.EstimatedSecondsRemaining is double eta&&double.IsFinite(future)?eta+future:null;
                        if(Environment.TickCount64-lastProgress>400){lastProgress=Environment.TickCount64;Publish("capturing",$"{index+1} / {Plan.Items.Count} \u00b7 {item.Name} \u00b7 {status.Message}",false);}
                    };
                    job.Start();
                    if(cancellation.IsCancellationRequested)await job.CancelAsync();
                    var result=await job.Completion;active=null;
                    cancellation.Token.ThrowIfCancellationRequested();
                    if(!result.Complete)throw new IOException(result.Message);
                    Publish("finalizing",$"Preparing {Plan.OutputResolution}p export: {item.Name}");
                    var profiled=await DeliverVideoProfile(file,Plan.OutputResolution,Plan.OutputQuality,Plan.Encoding,clipFolder,cancellation.Token);
                    item.File=await DeliverContainer(profiled,Plan.OutputFormat,clipFolder,cancellation.Token);
                    if(item.File!=file)File.Delete(file);
                    if(profiled!=file&&profiled!=item.File)File.Delete(profiled);
                    item.State="completed";item.Frames=result.Frames;item.DurationSeconds=result.DurationSeconds;item.Sha256=Hash(item.File);item.Error="";
                    Publish("capturing",$"Saved {index+1} of {Plan.Items.Count}: {item.Name}");
                }
                catch(OperationCanceledException){throw;}
                catch(Exception error)
                {
                    active=null;item.State="failed";item.Error=error.Message;Publish("capturing",$"Failed: {item.Name} \u00b7 {error.Message}");
                    if(!Plan.ContinueOnError)break;
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            Plan.ActiveIndex=-1;Plan.ActiveProgress=0;
            if(Plan.Items.Any(i=>i.State!="completed")){Plan.Finished=true;Publish("failed","Some films did not finish. Completed clips are safe; retry unfinished films to finish this job.");return;}
            if(Plan.Mode!="separate")
            {
                if(!await ReusableCombined(Plan.CombinedFile,Plan.CombinedSha256,cancellation.Token))
                {
                    Publish("joining","Joining clips and adding a chapter for each film\u2026");
                    Plan.CombinedFile=await Join(Plan,cancellation.Token);Plan.CombinedSha256=Hash(Plan.CombinedFile);
                }
            }
            Plan.Finished=true;Publish("completed",Plan.Mode=="separate"?$"Saved {Plan.Items.Count} MP4s.":Plan.Mode=="both"?$"Saved {Plan.Items.Count} MP4s and the combined reel.":"Saved your combined reel with film chapters.");
        }
        catch(OperationCanceledException)
        {
            if(Plan.ActiveIndex>=0&&Plan.Items[Plan.ActiveIndex].State!="completed")Plan.Items[Plan.ActiveIndex].State="cancelled";
            Plan.Finished=true;Publish("cancelled","Job cancelled. Completed clips are kept; retry to continue unfinished work.");
        }
        catch(Exception error){Plan.Finished=true;Plan.State="failed";Plan.Message=error.Message;if(jobLock!=null)Publish("failed",error.Message);else Changed?.Invoke(Plan);}
        finally{active=null;jobLock?.Dispose();}
    }
    public async Task CancelAsync()
    {
        cancellation.Cancel();var owned=active;if(owned!=null)await owned.CancelAsync();await Completion;
    }
    public void Dispose(){if(!IsRunning)cancellation.Dispose();}

    internal sealed record MediaInfo(double Seconds,long Frames,string Signature,int Chapters,double VideoStart=0,double AudioStart=0,double AudioSeconds=0,int FrameRate=60);
    internal static async Task<MediaInfo> Probe(string file,CancellationToken token)
    {
        var json=await Execute(FindTool("ffprobe")??throw new IOException("ffprobe.exe is missing."),new[]{"-v","error","-show_streams","-show_format","-show_chapters","-of","json",file},token);
        using var document=JsonDocument.Parse(json);var root=document.RootElement;
        var streams=root.GetProperty("streams").EnumerateArray().ToArray();
        var video=streams.FirstOrDefault(s=>s.GetProperty("codec_type").GetString()=="video");
        var audio=streams.FirstOrDefault(s=>s.GetProperty("codec_type").GetString()=="audio");
        if(video.ValueKind==JsonValueKind.Undefined||audio.ValueKind==JsonValueKind.Undefined)throw new IOException("A clip is missing its video or audio stream.");
        string V(JsonElement value,string key)=>value.TryGetProperty(key,out var property)?property.ToString():"";
        if(V(video,"codec_name")!="h264"||V(audio,"codec_name")!="aac")throw new IOException("Combined export requires the native H.264/AAC capture format.");
        var signature=string.Join("|",new[]{"codec_name","profile","width","height","pix_fmt","time_base","r_frame_rate"}.Select(k=>V(video,k)))+"/"+string.Join("|",new[]{"codec_name","sample_rate","channels","time_base"}.Select(k=>V(audio,k)));
        if(!double.TryParse(root.GetProperty("format").GetProperty("duration").GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out var duration)||duration<=0||!long.TryParse(V(video,"nb_frames"),out var frames)||frames<=0)throw new IOException("The clip has no valid duration or frame count.");
        if(!double.TryParse(V(video,"start_time"),NumberStyles.Float,CultureInfo.InvariantCulture,out var videoStart)||!double.TryParse(V(audio,"start_time"),NumberStyles.Float,CultureInfo.InvariantCulture,out var audioStart))throw new IOException("The clip has no valid stream origins.");
        if(!double.TryParse(V(audio,"duration"),NumberStyles.Float,CultureInfo.InvariantCulture,out var audioSeconds)||!double.IsFinite(audioSeconds)||audioSeconds<=0)throw new IOException("The clip has no valid audio duration.");
        var rate=V(video,"r_frame_rate");if(rate is not ("60/1" or "120/1"))throw new IOException("The clip frame rate is not 60 or 120 fps.");
        return new(duration,frames,signature,root.TryGetProperty("chapters",out var chapters)?chapters.GetArrayLength():0,videoStart,audioStart,audioSeconds,rate=="120/1"?120:60);
    }
    private static string MetadataText(string value)=>value.Replace("\\","\\\\").Replace("=","\\=").Replace(";","\\;").Replace("#","\\#").Replace("\r"," ").Replace("\n"," ");
    internal static async Task<bool> ReusableCombined(string file,string hash,CancellationToken token)
    {
        if(file.Length==0||!File.Exists(file))return false;
        try{
            if(Hash(file)!=hash)return false;
            var media=await Probe(file,token);
            return Math.Abs(media.VideoStart)<=1.0/48000&&Math.Abs(media.AudioStart)<=1.0/48000&&media.AudioSeconds+1.0/48000>=media.Frames/(double)media.FrameRate;
        }catch(IOException){return false;}
    }
    internal static async Task<string> Join(BatchPlan plan,CancellationToken token)
    {
        var infos=new List<MediaInfo>();foreach(var item in plan.Items){token.ThrowIfCancellationRequested();infos.Add(await Probe(item.File,token));}
        if(infos.Select(i=>i.Signature).Distinct().Count()!=1)throw new IOException("Capture streams differ. Create a new job with the same capture settings for every film.");
        var joinFolder=Path.Combine(plan.OutputDirectory,".parts");Directory.CreateDirectory(joinFolder);
        var attempt=Guid.NewGuid().ToString("N");var list=Path.Combine(joinFolder,attempt+".ffconcat");var metadata=Path.Combine(joinFolder,attempt+".ffmetadata");
        var concat=new StringBuilder("ffconcat version 1.0\n");var chapters=new StringBuilder(";FFMETADATA1\ntitle="+MetadataText(plan.Name)+"\n");double begin=0;
        for(int i=0;i<plan.Items.Count;i++)
        {
            concat.Append("file '").Append(Path.GetFullPath(plan.Items[i].File).Replace('\\','/').Replace("'","'\\''")).Append("'\n");
            double logicalSeconds=infos[i].Frames/(double)infos[i].FrameRate;
            concat.Append("duration ").Append(logicalSeconds.ToString("R",CultureInfo.InvariantCulture)).Append('\n');
            chapters.Append("[CHAPTER]\nTIMEBASE=1/48000\nSTART=").Append((long)Math.Round(begin*48000)).Append("\nEND=").Append((long)Math.Round((begin+logicalSeconds)*48000)).Append("\ntitle=").Append(MetadataText(Path.GetFileNameWithoutExtension(plan.Items[i].Name))).Append('\n');begin+=logicalSeconds;
        }
        File.WriteAllText(list,concat.ToString(),new UTF8Encoding(false));File.WriteAllText(metadata,chapters.ToString(),new UTF8Encoding(false));
        var temporary=Path.Combine(joinFolder,attempt+".partial.mp4");
        // Preserve the concat demuxer's timeline and AAC priming edit. Without
        // copyts FFmpeg shifts video by one AAC packet while audio starts at zero.
        await Execute(FindTool("ffmpeg")??throw new IOException("ffmpeg.exe is missing."),new[]{"-hide_banner","-v","error","-nostdin","-n","-copyts","-f","concat","-safe","0","-i",list,"-f","ffmetadata","-i",metadata,"-map","0:v:0","-map","0:a:0","-map_metadata","1","-map_chapters","1","-c","copy","-avoid_negative_ts","disabled","-movie_timescale","48000","-movflags","+faststart",temporary},token,Path.Combine(joinFolder,attempt+".log"));
        var merged=await Probe(temporary,token);
        if(merged.Frames!=infos.Sum(i=>i.Frames)||Math.Abs(merged.Seconds-infos.Sum(i=>i.Seconds))>0.25+infos.Count*0.05||merged.Chapters!=plan.Items.Count)throw new IOException("Combined MP4 did not pass frame, duration and chapter validation. Source clips are kept for retry.");
        if(Math.Abs(merged.VideoStart)>1.0/48000||Math.Abs(merged.AudioStart)>1.0/48000)throw new IOException("Combined MP4 stream origins are not aligned. Source clips are kept for retry.");
        if(merged.AudioSeconds+1.0/48000<merged.Frames/(double)merged.FrameRate)throw new IOException("Combined MP4 audio ends before its complete video sample range. Source clips are kept for retry.");
        token.ThrowIfCancellationRequested();
        var originalTemporary=temporary;
        temporary=await DeliverContainer(temporary,plan.OutputFormat,joinFolder,token);
        if(temporary!=originalTemporary)File.Delete(originalTemporary);
        var output=Path.Combine(plan.OutputDirectory,SafeName(plan.Name)+"."+plan.OutputFormat);
        for(int suffix=1;File.Exists(output);suffix++)output=Path.Combine(plan.OutputDirectory,$"{SafeName(plan.Name)} ({suffix}).{plan.OutputFormat}");
        File.Move(temporary,output,false);return output;
    }
    internal static void ValidateOutputFormat(string format)
    {
        if(format is not ("mp4" or "mov"))throw new IOException("Choose MP4 or MOV.");
    }
    internal static void ValidateVideoProfile(int resolution,string quality)
    {
        if(resolution is not (720 or 1080 or 1440 or 2160)||quality is not ("standard" or "high" or "maximum" or "custom"))
            throw new IOException("Choose 720p, 1080p, 1440p or 4K, and Standard, High, Maximum or Custom quality.");
    }
    internal static int VideoWidth(int resolution)=>resolution switch {720=>1280,1080=>1920,1440=>2560,2160=>3840,_=>throw new IOException("Unsupported export resolution.")};
    internal static async Task<string> DeliverVideoProfile(string source,int resolution,string quality,ExportEncoding encoding,string folder,CancellationToken token)
    {
        ValidateVideoProfile(resolution,quality);
        if(resolution==1440&&quality=="high")return source;
        Directory.CreateDirectory(folder);
        var temporary=Path.Combine(folder,Guid.NewGuid().ToString("N")+".partial.mp4");
        var output=Path.Combine(folder,Path.GetFileNameWithoutExtension(source)+$"-{resolution}p.mp4");
        try
        {
            await Execute(FindTool("ffmpeg")??throw new IOException("ffmpeg.exe is missing."),new[]{"-v","error","-nostdin","-n","-copyts","-i",source,"-map","0:v:0","-map","0:a:0","-map_metadata","0","-map_chapters","0","-vf",$"scale={VideoWidth(resolution)}:{resolution}:flags=lanczos,setsar=1"}
                .Concat(encoding.X264Arguments(quality)).Concat(new[]{"-video_track_timescale","15360","-c:a","copy","-avoid_negative_ts","disabled","-movie_timescale","48000","-movflags","+faststart",temporary}).ToArray(),token);
            var before=await Probe(source,token);var after=await Probe(temporary,token);
            if(before.Frames!=after.Frames||before.Chapters!=after.Chapters||Math.Abs(before.Seconds-after.Seconds)>.05||Math.Abs(before.VideoStart-after.VideoStart)>1.0/48000||Math.Abs(before.AudioStart-after.AudioStart)>1.0/48000||Math.Abs(before.AudioSeconds-after.AudioSeconds)>1.0/48000)
                throw new IOException("Export resizing did not preserve frames, audio timing or chapters. Original capture is kept.");
            using var media=JsonDocument.Parse(await Execute(FindTool("ffprobe")!,new[]{"-v","error","-select_streams","v:0","-show_streams","-of","json",temporary},token));
            var video=media.RootElement.GetProperty("streams")[0];
            if(video.GetProperty("width").GetInt32()!=VideoWidth(resolution)||video.GetProperty("height").GetInt32()!=resolution||video.GetProperty("r_frame_rate").GetString()!=$"{before.FrameRate}/1")throw new IOException("Export resolution or frame rate validation failed.");
            token.ThrowIfCancellationRequested();File.Move(temporary,output,false);return output;
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static async Task<string> DeliverContainer(string source,string format,string folder,CancellationToken token)
    {
        ValidateOutputFormat(format);
        if(format=="mp4")return source;
        Directory.CreateDirectory(folder);
        var stem=Path.GetFileNameWithoutExtension(source);
        if(stem.EndsWith(".partial",StringComparison.OrdinalIgnoreCase))stem=stem[..^8];
        var output=Path.Combine(folder,stem+"."+format);
        var temporary=Path.Combine(folder,Guid.NewGuid().ToString("N")+".partial."+format);
        try
        {
            await Execute(FindTool("ffmpeg")??throw new IOException("ffmpeg.exe is missing."),new[]{"-v","error","-nostdin","-n","-copyts","-i",source,"-map","0:v:0","-map","0:a:0","-map_metadata","0","-map_chapters","0","-c","copy","-avoid_negative_ts","disabled","-movie_timescale","48000","-movflags","+faststart",temporary},token);
            var before=await Probe(source,token);var after=await Probe(temporary,token);
            if(before.Frames!=after.Frames||before.Chapters!=after.Chapters||Math.Abs(before.Seconds-after.Seconds)>.05||Math.Abs(before.VideoStart-after.VideoStart)>1.0/48000||Math.Abs(before.AudioStart-after.AudioStart)>1.0/48000||Math.Abs(before.AudioSeconds-after.AudioSeconds)>1.0/48000)
                throw new IOException("Export format conversion did not preserve video, audio timing or chapters. Original capture is kept.");
            token.ThrowIfCancellationRequested();File.Move(temporary,output,false);return output;
        }
        finally {if(File.Exists(temporary))File.Delete(temporary);}
    }
    internal static async Task<string> Execute(string executable,IEnumerable<string> arguments,CancellationToken token,string? log=null)
    {
        token.ThrowIfCancellationRequested();
        var info=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var argument in arguments)info.ArgumentList.Add(argument);
        using var process=Process.Start(info)??throw new IOException("Unable to start "+Path.GetFileName(executable));
        using var registration=token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}});
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();var output=await stdout;var error=await stderr;
        if(log!=null)File.WriteAllText(log,error);
        token.ThrowIfCancellationRequested();
        if(process.ExitCode!=0)throw new IOException(Path.GetFileName(executable)+" failed: "+error[..Math.Min(error.Length,1500)]);
        return output;
    }
}

internal static class BatchRecordingCli
{
    public static bool ShouldRun(string[] args)=>Array.IndexOf(args,"--batch-job")>=0;
    public static async Task Run(string[] args)
    {
        string? Arg(string name){var i=Array.IndexOf(args,name);return i>=0&&i+1<args.Length?args[i+1]:null;}
        try
        {
            var path=Path.GetFullPath(Arg("--batch-job")??throw new IOException("--batch-job requires a JSON job file."));
            var plan=BatchRecording.Read(path);
            var executable=Path.GetFullPath(Arg("--native-host")??Path.Combine(AppContext.BaseDirectory,"native","NativeReplayBackend.exe"));
            using var job=new BatchRecording(plan,executable,path);job.Start();
            if(Arg("--cancel-file") is string cancelFile)
            {
                cancelFile=Path.GetFullPath(cancelFile);
                while(!job.Completion.IsCompleted)
                {
                    if(File.Exists(cancelFile)){await job.CancelAsync();break;}
                    await Task.WhenAny(job.Completion,Task.Delay(500));
                }
            }
            await job.Completion;
            Environment.ExitCode=job.Plan.State=="completed"?0:1;
            if(Arg("--report") is string report)BatchRecording.Save(Path.GetFullPath(report),job.Plan);
        }
        catch(Exception error)
        {
            Environment.ExitCode=1;
            if(Arg("--report") is string report)BatchRecording.Save(Path.GetFullPath(report),new BatchPlan{State="failed",Message=error.Message,Finished=true});
        }
    }
}
