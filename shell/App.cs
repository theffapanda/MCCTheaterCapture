using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
#if !NATIVE_UI_BETA
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
#endif
using Microsoft.Win32;

namespace MCCTheaterCapture;

internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
#if SINGLE_FILE_BUILD
        // Bundle extraction paths change with each build. Keep user data stable.
        if (!args.Contains("--data-dir"))
            args = args.Concat(new[] { "--data-dir", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MCCTheaterCapture", "UserData") }).ToArray();
#endif
        if(HighlightCli.ShouldRun(args)){HighlightCli.Run(args).GetAwaiter().GetResult();return;}
        if(BatchRecordingCli.ShouldRun(args)){BatchRecordingCli.Run(args).GetAwaiter().GetResult();return;}
        if(BackgroundRecordingCli.ShouldRun(args)){BackgroundRecordingCli.Run(args).GetAwaiter().GetResult();return;}
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        app.Run(new ViewerWindow(args));
    }

    internal static string? SettingsIniPath(string[] args)
    {
        int index=Array.IndexOf(args,"--settings-ini");
        if(index<0)return null;
        if(index+1>=args.Length||string.IsNullOrWhiteSpace(args[index+1])||args[index+1].StartsWith("--",StringComparison.Ordinal))
            throw new IOException("--settings-ini requires the path to MCC's GameUserSettings.ini.");
        return Path.GetFullPath(args[index+1]);
    }

    internal static int FovDegrees(string[] args,int saved=91)
    {
        int index=Array.IndexOf(args,"--fov");
        if(index<0)return saved is >=55 and <=120?saved:91;
        if(index+1>=args.Length||!int.TryParse(args[index+1],out var selected)||selected<55||selected>120)
            throw new IOException("--fov requires an integer from 55 to 120.");
        return selected;
    }
}

internal sealed class Preferences
{
    public string Movies { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "LocalLow", "MCC", "Temporary", "UserContent");
    public string Maps { get; set; } = "";
    public List<string> Recent { get; set; } = new();
    public bool ShowPlayerName { get; set; }
    public bool BackgroundRecording { get; set; }
    public int FovDegrees { get; set; } = 91;
    public string BatchOutputRoot { get; set; } = Path.Combine(MCCTheaterCapture.BackgroundRecording.DownloadsFolder(),"Theater Exports");
}

internal sealed record Recording(string Id, string Name, string Map, long Bytes, string Modified, bool CanDecode,
    string? Reason, string? GameType, string? GameVariantName, string Duration, int TotalTicks, string Path)
{
    public string Title { get; init; } = "halo3";
    public string TitleName { get; init; } = "Halo 3";
    public string ArtworkKey { get; init; } = "";
}

internal sealed partial class ViewerWindow : Window
{
    private const string Origin = "https://mcc-theater-capture.local";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(18, 24, 23));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(231, 187, 114));
    private readonly string[] args;
    private readonly string dataRoot, nativeHost;
#if NATIVE_UI_BETA
    private readonly Grid browser = new();
#else
    private readonly WebView2 browser = new();
#endif
    private readonly Grid body = new();
    private readonly FrameworkElement playbackToolbar, playbackViewControls;
    private string? artworkMaps;
    private Dictionary<string, string> artwork = new();
    private readonly TextBlock status = Text("Preparing capture preview\u2026", 12);
    private readonly TextBlock activeFov = Text("FOV applies when a film opens",12);
    private readonly TextBlock resourceUsage = Text("Tool CPU: measuring\u2026 \u00b7 GPU: measuring\u2026",12);
    private readonly ToolResourceMonitor resourceMonitor = new();
    private readonly DispatcherTimer resourceTimer = new() { Interval=TimeSpan.FromSeconds(1) };
    private Task<ToolResourceUsage>? resourceSample;
    private readonly Dictionary<string, Recording> recordings = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly List<object> diagnosticEvents = new();
    private Preferences preferences = new();
    private NativeSurface? surface;
    private FrameworkElement? playback;
    private Button? play, orbit, zoomIn, zoomOut, cameraOptions, recordedCamera,recordMp4,stopRecording;
    private CheckBox? showPlayerName,nativeScoreboard;
    private MenuItem? centerOrbit;
    private TextBlock? transportTime, metrics, nativeState, cameraHint,renderDimensions,recordingStatus;
    private PlayersPane? playersPane;
    private ProgressBar? progress;
    private Recording? current;
    private bool ready, closing, closeApproved, switching, paused, orbitEnabled, exerciseStarted, nativeSceneReady, playerPovSupported, playersExerciseStarted;
    private int firstTick = -1, lastTick = -1, telemetryCount;
    private int nativeGameState,watchedPlayerDatum=-1,watchCommandVersion;
    private int renderWidth,renderHeight;
    private bool? watchCommandAccepted;
    private bool childAttached;
    private bool playerNameVisibilitySupported,playerNameVisible,autoRecordStarted;
    private bool? lastPlayerNameRequest;
    private string recordingState="idle",recordingFile="";
    private double recordingDuration;
    private long recordingFrames,recordingDroppedFrames;
    private bool? audioCaptureSupported,recordingAudio;
    private string audioCaptureError="",requiredAudioFailure="";
    private bool scoreboardSupported,scoreboardVisible;
    private const string GameAudioLabel="Game audio \u00b7 Native playback process only";
    private BackgroundRecording? backgroundRecording;
    private BackgroundRecordingStatus? backgroundStatus;
    private BatchRecording? batchRecording;
    private BatchPlan batchDraft = new();
    private string DraftPath => Path.Combine(dataRoot,"batch-draft.json");

    public ViewerWindow(string[] args)
    {
        this.args = args;
        dataRoot = Path.GetFullPath(Argument("--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "UserData"));
#if NATIVE_UI_BETA
        dataRoot = Path.GetFullPath(Argument("--data-dir") ?? Path.Combine(AppContext.BaseDirectory, "BetaUserData"));
#endif
        nativeHost = Path.GetFullPath(Argument("--native-host") ?? Path.Combine(AppContext.BaseDirectory, "native", "NativeReplayBackend.exe"));
        Directory.CreateDirectory(dataRoot);
        LoadPreferences();LoadHighlights();
        preferences.FovDegrees=Program.FovDegrees(args,preferences.FovDegrees);
        try{if(File.Exists(DraftPath))batchDraft=BatchRecording.Read(DraftPath);else batchDraft.Fov=preferences.FovDegrees;}catch(Exception error)when(error is IOException or JsonException){batchDraft=new(){Fov=preferences.FovDegrees};}
        if(Guid.TryParseExact(batchDraft.Id,"N",out _))
        {
            var latest=Path.Combine(dataRoot,"batches",batchDraft.Id,"job.json");
            try{if(File.Exists(latest))batchDraft=BatchRecording.Read(latest);}catch(Exception error)when(error is IOException or JsonException){}
        }
        if(bool.TryParse(Argument("--hide-player-name"),out var hidePlayerName))preferences.ShowPlayerName=!hidePlayerName;
        if(bool.TryParse(Argument("--background"),out var background))preferences.BackgroundRecording=background;
        if (Argument("--movies") is string movies) preferences.Movies = Path.GetFullPath(movies);
        if (Argument("--maps") is string maps) preferences.Maps = Path.GetFullPath(maps);
        if (Argument("--mcc-root") is string mcc) preferences.Maps = FilmTitle.InstallationRoot(mcc);
        if (!Directory.Exists(preferences.Maps)) preferences.Maps = FindMaps() ?? "";
        InitializeMonitoring();
        Title = "MCC Theater Capture"; Width=1600; Height=1000; MinWidth=1000; MinHeight=680;
#if NATIVE_UI_BETA
        Title += " — Native UI Beta";
#endif
        SourceInitialized += (_,_) => ApplyWindowAppearance();
        WindowStartupLocation=WindowStartupLocation.CenterScreen; Background=new SolidColorBrush(Color.FromRgb(12,17,24));
        var layout = new DockPanel();
        var toolbar = new DockPanel { Background=PanelBrush, LastChildFill=true };
        playbackToolbar=toolbar; toolbar.Visibility=Visibility.Collapsed;
        var buttons = new StackPanel { Orientation=Orientation.Horizontal, Margin=new Thickness(12,8,10,8) };
        buttons.Children.Add(new TextBlock { Text="MCC THEATER CAPTURE", Foreground=Accent, FontSize=11, FontWeight=FontWeights.SemiBold, VerticalAlignment=VerticalAlignment.Center, Margin=new Thickness(2,0,20,0) });
        buttons.Children.Add(Button("Library", ShowLibrary));
        var open=Button("Open recording\u2026", async () => { var file=PickFilm(); if(file!=null) await OpenRecording(file); }); open.Background=Accent; open.Foreground=Brushes.Black; buttons.Children.Add(open);
        buttons.Children.Add(Button("Theater folder\u2026", async () => { PickFolder(false); await ShowLibrary(); }));
        buttons.Children.Add(Button("Map folder\u2026", async () => { PickFolder(true); await ShowLibrary(); }));
        DockPanel.SetDock(buttons,Dock.Left); toolbar.Children.Add(buttons); status.Margin=new Thickness(12,0,12,0); status.VerticalAlignment=VerticalAlignment.Center; toolbar.Children.Add(status);
        DockPanel.SetDock(toolbar,Dock.Top); layout.Children.Add(toolbar);
        var viewControls=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(14,5,14,7)};
        playbackViewControls=viewControls; viewControls.Visibility=Visibility.Collapsed;
        var fovLabel=Text($"FOV {preferences.FovDegrees}\u00b0",12);fovLabel.Width=70;fovLabel.VerticalAlignment=VerticalAlignment.Center;
        var fovSlider=new Slider{Minimum=55,Maximum=120,Value=preferences.FovDegrees,TickFrequency=1,IsSnapToTickEnabled=true,SmallChange=1,LargeChange=5,Width=220,VerticalAlignment=VerticalAlignment.Center,ToolTip="Horizontal FOV for on-foot and vehicle views. Applies when a film opens; restart the current film to apply."};
        System.Windows.Automation.AutomationProperties.SetName(fovSlider,"Field of view in degrees");
        fovSlider.ValueChanged+=(_,e)=>{preferences.FovDegrees=(int)Math.Round(e.NewValue);fovLabel.Text=$"FOV {preferences.FovDegrees}\u00b0";SavePreferences();};
        viewControls.Children.Add(fovLabel);viewControls.Children.Add(fovSlider);
        viewControls.Children.Add(Button("Apply FOV / restart film",async()=>{
            if(IsRecordingActive||backgroundRecording?.IsRunning==true)throw new IOException("Finish or stop recording before restarting the film to apply FOV.");
            if(current!=null)await OpenRecording(current.Path);
        }));
        activeFov.VerticalAlignment=VerticalAlignment.Center;viewControls.Children.Add(activeFov);
        DockPanel.SetDock(viewControls,Dock.Top);layout.Children.Add(viewControls);
        resourceUsage.Margin=new Thickness(14,4,14,7);
        resourceUsage.ToolTip="App and child processes. CPU is a percentage of total logical CPU capacity. GPU is the busiest owned GPU engine. Unavailable counters show N/A.";
        DockPanel.SetDock(resourceUsage,Dock.Bottom);layout.Children.Add(resourceUsage);
        body.Children.Add(browser); layout.Children.Add(body); Content=layout;
        resourceTimer.Tick+=async(_,_)=>
        {
            resourceTimer.Stop();
            try
            {
                resourceSample=Task.Run(resourceMonitor.Sample);
                var usage=await resourceSample;
                if(closing)return;
                string Percent(double? value)=>value.HasValue?$"{value:F1}%":"N/A";
                resourceUsage.Text=$"Tool CPU: {Percent(usage.CpuPercent)} \u00b7 GPU: {Percent(usage.GpuPercent)}";
            }
            catch(Exception error) when(error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { resourceUsage.Text="Tool CPU: N/A \u00b7 GPU: N/A"; }
            finally { if(!closing)resourceTimer.Start(); }
        };
        resourceTimer.Start();
        Closed+=(_,_)=>{resourceTimer.Stop();};
        Loaded += async (_,_) => await Initialize();
        Closing += async (_,e) => {
            if(closeApproved)return;
            e.Cancel=true;
            if(closing)return;
            closing=true; IsEnabled=false;
            try { await StopMonitoring(); } catch(Exception error) { Record("monitor-stop-error",new{error.Message}); }
            resourceTimer.Stop();
            if(resourceSample!=null)try{await resourceSample;}catch(Exception){}
            resourceMonitor.Dispose();
            highlightScanCancellation?.Cancel();
            if(highlightRecording?.IsRunning==true)await highlightRecording.CancelAsync();
            if(batchRecording?.IsRunning==true)await batchRecording.CancelAsync();
            if(backgroundRecording?.IsRunning==true)await backgroundRecording.CancelAsync();
            await StopPlayback(); WriteReport(); SavePreferences();
#if !NATIVE_UI_BETA
            browser.Dispose();
#endif
            backgroundRecording?.Dispose(); closeApproved=true;
            // Cleanup can finish synchronously; wait for this Closing event to unwind.
            var closeOperation = Dispatcher.BeginInvoke(new Action(Close));
        };
    }

    private string? Argument(string name) { var index=Array.IndexOf(args,name); return index>=0&&index+1<args.Length?args[index+1]:null; }
    private static TextBlock Text(string value,double size=13) => new() { Text=value, FontSize=size, Foreground=Brushes.LightGray, TextWrapping=TextWrapping.Wrap };
    private Button Button(string label,Func<Task> action)
    {
        var button=new Button { Content=label, Padding=new Thickness(13,8,13,8), Margin=new Thickness(0,0,7,0), Background=new SolidColorBrush(Color.FromRgb(33,48,61)), Foreground=Brushes.White, BorderBrush=new SolidColorBrush(Color.FromRgb(55,71,87)) };
        button.Click+=async(_,_)=>{ if(switching||closing)return; try{await action();}catch(Exception error){ShowError(error.Message);} }; return button;
    }
    private void ShowError(string error) { status.Text=error; Record("error",new{message=error}); }
    private void HandleNativeFailure(string error)
    {
        ShowError(error);
        if(!nativeSceneReady)FinishRecordingAutomation(true);
    }
    private void Record(string type,object? detail=null)
    {
        var item=new {type,elapsedSeconds=clock.Elapsed.TotalSeconds,utc=DateTimeOffset.UtcNow,detail};
        diagnosticEvents.Add(item);
        try { File.AppendAllText(Path.Combine(dataRoot,"shell-events.jsonl"),JsonSerializer.Serialize(item,Json)+Environment.NewLine); }catch(IOException){}
    }

    private async Task Initialize()
    {
        try
        {
#if NATIVE_UI_BETA
            BuildNativeDesk();
            ready=true; await ShowLibrary(); Record("library-ready", new { ui="native-wpf-beta" });
            if (args.Contains("--exercise-native-ui")) await ExerciseNativeDesk();
            if (args.Contains("--exercise-native-export")) await ExerciseNativeExport();
#else
            var fixedRuntime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
            // Single-file builds extract managed content separately from the executable.
            if (!Directory.Exists(fixedRuntime))
                fixedRuntime = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "WebView2Runtime");
            // Fixed WebView2 requires these folder permissions for its renderer
            // sandbox on Windows 10. This changes only our extracted runtime folder.
            if (Directory.Exists(fixedRuntime) && Environment.OSVersion.Version.Build < 22000)
            {
                var permissions = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icacls.exe"))
                    { UseShellExecute = false, CreateNoWindow = true };
                foreach (var argument in new[] { fixedRuntime, "/grant", "*S-1-15-2-2:(OI)(CI)(RX)", "*S-1-15-2-1:(OI)(CI)(RX)" })
                    permissions.ArgumentList.Add(argument);
                using var process = Process.Start(permissions) ?? throw new IOException("Could not prepare bundled browser permissions.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) throw new IOException("Could not grant browser access to the extracted WebView2Runtime folder.");
            }
            var environment=await CoreWebView2Environment.CreateAsync(
                Directory.Exists(fixedRuntime) ? fixedRuntime : null,Path.Combine(dataRoot,"WebView2"));
            await browser.EnsureCoreWebView2Async(environment);
            var core=browser.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled=false; core.Settings.IsStatusBarEnabled=false; core.Settings.AreDevToolsEnabled=false;
            core.SetVirtualHostNameToFolderMapping(new Uri(Origin).Host,Path.Combine(AppContext.BaseDirectory,"web"),CoreWebView2HostResourceAccessKind.DenyCors);
            var artworkCache=Path.Combine(dataRoot,"MapArtwork");
            Directory.CreateDirectory(artworkCache);
            core.SetVirtualHostNameToFolderMapping(MapArtwork.Host,artworkCache,CoreWebView2HostResourceAccessKind.DenyCors);
            RegisterAutoClipStreaming(core);
            core.WebMessageReceived+=MessageReceived;
            core.NavigationStarting+=(_,e)=> { if(!e.Uri.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))e.Cancel=true; };
            core.NewWindowRequested+=(_,e)=>e.Handled=true;
            ready=true; await ShowLibrary(); Record("library-ready");
#endif
            StartMonitoring();
            if(Argument("--film") is string film)await OpenRecording(film);
            if(double.TryParse(Argument("--auto-close-seconds"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds)&&seconds>0)
            {
                var timer=new DispatcherTimer {Interval=TimeSpan.FromSeconds(seconds)}; timer.Tick+=(_,_)=>{timer.Stop();Close();}; timer.Start();
            }
        }
        catch(Exception error){ ShowError(error.Message);FinishRecordingAutomation(true); }
    }

#if !NATIVE_UI_BETA
    private async void MessageReceived(object? sender,CoreWebView2WebMessageReceivedEventArgs e)
    {
        int id=0;
        try
        {
            if(!e.Source.StartsWith(Origin+"/",StringComparison.OrdinalIgnoreCase))return;
            using var document=JsonDocument.Parse(e.WebMessageAsJson); var request=document.RootElement;
            id=request.GetProperty("id").GetInt32(); object? result=null;
            switch(request.GetProperty("type").GetString())
            {
                case "monitor-settings": case "monitor-export": case "monitor-cancel": case "monitor-folder": case "monitor-archive": case "monitor-retry-scan": case "monitor-dismiss": case "monitor-open-clip":
                    result=await MonitoringMessage(request);break;
                case "autoclips-list": case "autoclips-folder": case "autoclips-open": case "autoclips-watch": case "autoclips-reveal": case "autoclips-rename": case "autoclips-delete":
                    result=await AutoClipMessage(request);break;
                case "highlights-state": case "highlights-scan": case "highlights-cancel-scan": case "highlights-start": case "highlights-save": case "highlights-retry": case "highlights-cancel": case "highlights-load": case "highlights-open-folder": case "highlights-open-clip":
                    result=await HighlightMessage(request);break;
                case "catalog": result=Catalog(); break;
                case "batch-state":result=BatchState();break;
                case "batch-save":
                    batchDraft=request.GetProperty("plan").Deserialize<BatchPlan>(BatchRecording.Json)??throw new IOException("Missing batch plan.");
                    BatchRecording.Save(DraftPath,batchDraft);break;
                case "batch-pick-films":result=PickBatchFilms();break;
                case "batch-drop-films":
                    if(HighlightRunning||batchRecording?.IsRunning==true)throw new IOException("Finish the current scan or export before adding more films.");
                    result=ImportBatchFilms(e.AdditionalObjects.OfType<CoreWebView2File>().Select(file=>file.Path));break;
                case "batch-output-folder":
                    var outputDialog=new OpenFolderDialog{Title="Choose where batch exports will be saved",Multiselect=false};
                    if(Directory.Exists(preferences.BatchOutputRoot))outputDialog.InitialDirectory=preferences.BatchOutputRoot;
                    if(outputDialog.ShowDialog(this)==true)
                    {
                        preferences.BatchOutputRoot=outputDialog.FolderName;SavePreferences();
                        // A started job freezes its destination. If it never delivered a film (e.g. it failed because the
                        // folder was not writable), move it to a fresh draft so the new folder is actually used.
                        if(batchRecording?.IsRunning!=true&&batchDraft.Signature.Length>0&&batchDraft.CombinedFile.Length==0&&!batchDraft.Items.Any(i=>i.State=="completed"))
                        {
                            batchDraft=NativeBetaDraft.Fork(batchDraft);BatchRecording.Save(DraftPath,batchDraft);
                            batchRecording?.Dispose();batchRecording=null;
                        }
                    }
                    result=BatchState();break;
                case "batch-start":
                    var submitted=request.GetProperty("plan").Deserialize<BatchPlan>(BatchRecording.Json)??throw new IOException("Missing batch plan.");
                    await StartBatch(submitted);result=BatchState();break;
                case "batch-cancel":if(batchRecording?.IsRunning==true)await batchRecording.CancelAsync();result=BatchState();break;
                case "batch-retry":
                    if(batchRecording==null)throw new IOException("Load a saved job before retrying.");
                    await StartBatch(batchRecording.Plan);result=BatchState();break;
                case "batch-export-job":
                    var exportPlan=request.GetProperty("plan").Deserialize<BatchPlan>(BatchRecording.Json)??throw new IOException("Missing batch plan.");
                    PrepareBatch(exportPlan);BatchRecording.Validate(exportPlan);
                    var saveDialog=new SaveFileDialog{Title="Save a headless batch job",Filter="Batch job (*.json)|*.json",FileName=BatchRecording.SafeName(exportPlan.Name)+".json",AddExtension=true,DefaultExt=".json"};
                    if(saveDialog.ShowDialog(this)==true){BatchRecording.Save(saveDialog.FileName,exportPlan);result=saveDialog.FileName;}break;
                case "batch-load-job":
                    if(batchRecording?.IsRunning==true)throw new IOException("Wait for the current batch or cancel it before loading another job.");
                    var jobDialog=new OpenFileDialog{Title="Load a batch job or checkpoint",Filter="Batch job (*.json)|*.json",CheckFileExists=true};
                    if(jobDialog.ShowDialog(this)==true){batchDraft=BatchRecording.Read(jobDialog.FileName);BatchRecording.Save(DraftPath,batchDraft);batchRecording?.Dispose();batchRecording=null;result=batchDraft;}break;
                case "batch-open-folder":
                    var directory=batchRecording?.Plan.OutputDirectory??batchDraft.OutputDirectory;
                    if(directory!=null&&Directory.Exists(directory))Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={directory}});break;
                case "pick-film": var path=PickFilm(); if(path!=null)await OpenRecording(path); break;
                case "pick-maps": PickFolder(true); break;
                case "pick-movies": PickFolder(false); break;
                case "background-recording-preference":preferences.BackgroundRecording=request.GetProperty("enabled").GetBoolean();SavePreferences();break;
                case "background-cancel":if(backgroundRecording?.IsRunning==true)await backgroundRecording.CancelAsync();break;
                case "background-open-folder":backgroundRecording?.OpenFolder();break;
                case "open-film": if(!recordings.TryGetValue(request.GetProperty("filmId").GetString()!,out var film))throw new IOException("The recording is no longer available.");
                    // preview=true always opens interactive playback, even when "Export directly to file" is on.
                    await OpenRecording(film.Path,request.TryGetProperty("preview",out var preview)&&preview.ValueKind==JsonValueKind.True); break;
                default: throw new IOException("Unknown capture action.");
            }
            if(!closing)browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok=true,data=result},Json));
        }
        catch(Exception error){ if(!closing)browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{id,ok=false,error=error.Message},Json)); ShowError(error.Message); }
    }

#endif
    private async Task ShowLibrary()
    {
        if(!ready)return;
        switching=true;
        try { await StopPlayback(); playbackToolbar.Visibility=playbackViewControls.Visibility=Visibility.Collapsed; browser.Visibility=Visibility.Visible;
#if NATIVE_UI_BETA
            RefreshNativeLibrary(); RefreshNativeDesk();
#else
            browser.CoreWebView2.Navigate(Origin+"/home.html");
#endif
            status.Text="Original films \u00b7 Native Halo rendering"; }
        finally{switching=false;}
    }
    private async Task StopPlayback()
    {
        var owned=surface;
        bool finalizing=IsRecordingActive;
        if(owned!=null&&finalizing)
        {
            owned.Command(new{command="recordStop"});
            if(recordingStatus!=null)recordingStatus.Text="Finalizing MP4\u2026 "+GameAudioLabel;
            var wait=Stopwatch.StartNew();while(IsRecordingActive&&wait.Elapsed<TimeSpan.FromSeconds(30))await Task.Delay(100);
            if(IsRecordingActive)Record("recording-finalize-wait-expired",new{recordingFile,recordingState});
        }
        surface=null;
        if(owned!=null){ await owned.StopAsync(finalizing); owned.Dispose(); }
        if(playback!=null){body.Children.Remove(playback);playback=null;}playersPane=null;
    }

    private async Task OpenRecording(string path,bool forcePlayback=false)
    {
        if(HighlightRunning)throw new IOException("Finish the highlight scan or export before opening playback.");
        if(!ready||switching||closing)return;
        if(batchRecording?.IsRunning==true)throw new IOException("A batch job is running. Wait for it to finish or cancel it before opening a film.");
        if(backgroundRecording?.IsRunning==true)throw new IOException("A background recording is already running. Wait for it to finish or cancel it before opening another film.");
        if(!Directory.Exists(preferences.Maps))PickFolder(true);
        var film=ReadRecording(path);
        if(Argument("--title") is string requestedTitle&&FilmTitle.FromId(requestedTitle).Id!=film.Title)throw new IOException("The requested title does not match the film header.");
        if(!film.CanDecode)throw new IOException(film.Reason);
        if(!File.Exists(nativeHost))throw new FileNotFoundException("Build the native proof of concept before opening a recording.",nativeHost);
        if(preferences.BackgroundRecording&&!forcePlayback){await StartBackgroundRecording(film);return;}
        switching=true;
        try
        {
            await StopPlayback(); current=film; paused=false; orbitEnabled=false; childAttached=false; firstTick=lastTick=-1; telemetryCount=0; exerciseStarted=false; nativeSceneReady=false;playerPovSupported=false;playersExerciseStarted=false;watchedPlayerDatum=-1;watchCommandVersion=0;watchCommandAccepted=null;nativeGameState=0;renderWidth=renderHeight=0;
            playerNameVisibilitySupported=false;playerNameVisible=true;lastPlayerNameRequest=null;autoRecordStarted=false;recordingState="idle";recordingFile="";recordingDuration=0;recordingFrames=recordingDroppedFrames=0;audioCaptureSupported=recordingAudio=null;audioCaptureError=requiredAudioFailure="";scoreboardSupported=scoreboardVisible=false;
            preferences.Recent.RemoveAll(p=>string.Equals(p,film.Path,StringComparison.OrdinalIgnoreCase));preferences.Recent.Insert(0,film.Path);preferences.Recent=preferences.Recent.Take(30).ToList();SavePreferences();
            var mccRoot=FilmTitle.InstallationRoot(preferences.Maps);
            var output=Path.Combine(dataRoot,"sessions",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
            activeFov.Text="Loading selected FOV\u2026";
            surface=new NativeSurface(nativeHost,film.Path,mccRoot,output,Program.SettingsIniPath(args),preferences.FovDegrees); surface.NativeEvent+=HandleNativeEvent; surface.Failure+=HandleNativeFailure;
            surface.ChildAttached+=(parent,child)=>{childAttached=true;Record("native-child-attached",new{parent=parent.ToInt64(),child=child.ToInt64()});};
            playback=CreatePlayback(film,surface); browser.Visibility=Visibility.Collapsed;playbackToolbar.Visibility=playbackViewControls.Visibility=Visibility.Visible;body.Children.Add(playback);
            try { var roster=film.Title=="halo3"?RecordedPlayers.Read(film.Path):Array.Empty<RecordedPlayer>();playersPane?.LoadHeader(roster);Record("film-roster",new{source="film-header",players=roster}); }
            catch(IOException error){playersPane?.LoadHeader(Array.Empty<RecordedPlayer>());Record("film-roster-unavailable",new{message=error.Message});}
            status.Text=$"Loading {film.Name} \u00b7 {film.Map}";Record("film-open",new{film=film.Path,film.Map,backend=nativeHost,output});
        }
        finally{switching=false;}
    }

    private async Task StartBackgroundRecording(Recording film)
    {
        if(MonitorExportRunning)throw new IOException("Pause the automatic clip export before starting another recording.");
        await ShowLibrary();current=film;
        preferences.Recent.RemoveAll(path=>string.Equals(path,film.Path,StringComparison.OrdinalIgnoreCase));preferences.Recent.Insert(0,film.Path);preferences.Recent=preferences.Recent.Take(30).ToList();SavePreferences();
        var mccRoot=FilmTitle.InstallationRoot(preferences.Maps);
        double? seconds=null;
        if(Array.IndexOf(args,"--record-seconds")>=0)
        {
            string? duration=Argument("--record-seconds");
            if(!double.TryParse(duration,System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var requested)||!double.IsFinite(requested)||requested<=0||requested>43200)
                throw new IOException("--record-seconds must be a positive finite duration no greater than 43200 seconds. Omit it to record the full film.");
            seconds=requested;
        }
        var session=Path.Combine(dataRoot,"background",DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff"));
        backgroundRecording?.Dispose();
        backgroundRecording=new BackgroundRecording(nativeHost,film.Path,mccRoot,session,seconds,settingsIni:Program.SettingsIniPath(args),fov:preferences.FovDegrees);
        var owned=backgroundRecording;backgroundStatus=owned.Status;
        owned.Changed+=value=>Dispatcher.BeginInvoke(()=>
        {
            if(backgroundRecording!=owned)return;backgroundStatus=value;status.Text=value.Message;
            Record("background-recording",value);PublishBackgroundStatus();
        });
        Record("background-start-request",new{film=film.Path,file=owned.Status.File,session,recordSeconds=seconds,audio=true,playerNameVisible=false});
        PublishBackgroundStatus();owned.Start();_ = CompleteBackgroundRecording(owned);
    }
    private async Task CompleteBackgroundRecording(BackgroundRecording owned)
    {
        var result=await owned.Completion;if(backgroundRecording!=owned)return;backgroundStatus=result;PublishBackgroundStatus();
        if(!result.Cancelled)FinishRecordingAutomation(!result.Complete);
    }
    private void PublishBackgroundStatus()
    {
        if(!ready||closing)return;
#if NATIVE_UI_BETA
        RefreshNativeDesk();
#else
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="background-recording",backgroundRecording=preferences.BackgroundRecording,job=backgroundStatus},Json));
#endif
    }

    private object BatchState()=>new{draft=batchDraft,job=batchRecording?.Plan,running=batchRecording?.IsRunning==true,outputRoot=preferences.BatchOutputRoot,fov=preferences.FovDegrees,toolsReady=BatchRecording.FindTool("ffmpeg")!=null&&BatchRecording.FindTool("ffprobe")!=null};
    private void PublishBatch()
    {
        if(!ready||closing)return;
#if NATIVE_UI_BETA
        RefreshNativeDesk();
#else
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new{type="batch-status",data=BatchState()},Json));
#endif
    }
    private void PrepareBatch(BatchPlan plan)
    {
        if(plan.Signature.Length>0)return; // A retry keeps its original settings and destination.
        if(!Guid.TryParseExact(plan.Id,"N",out _))throw new IOException("Invalid job ID.");
        plan.Name=string.IsNullOrWhiteSpace(plan.Name)?"Theater reel":plan.Name.Trim();
        plan.MccRoot=string.IsNullOrWhiteSpace(preferences.Maps)?"":FilmTitle.InstallationRoot(preferences.Maps);
        plan.SettingsIni=Program.SettingsIniPath(args);
        plan.OutputDirectory=Path.Combine(preferences.BatchOutputRoot,BatchRecording.SafeName(plan.Name)+"-"+plan.Id[..8]);
    }
    private async Task StartBatch(BatchPlan plan)
    {
        if(HighlightRunning||batchRecording?.IsRunning==true||backgroundRecording?.IsRunning==true||IsRecordingActive)throw new IOException("Finish the current recording or batch before starting another job.");
        PrepareBatch(plan);BatchRecording.Validate(plan);
        if(surface!=null)await ShowLibrary();
        batchRecording?.Dispose();
        var checkpoint=Path.Combine(dataRoot,"batches",plan.Id,"job.json");
        var owned=new BatchRecording(plan,nativeHost,checkpoint);batchRecording=owned;
        batchDraft=plan;BatchRecording.Save(DraftPath,plan);
        owned.Changed+=_=>{if(!closing)Dispatcher.BeginInvoke(()=>{if(batchRecording==owned)PublishBatch();});};
        owned.Start();PublishBatch();_ = CompleteBatch(owned);
    }
    private async Task CompleteBatch(BatchRecording owned)
    {
        await owned.Completion;
        if(batchRecording!=owned)return;
        batchDraft=owned.Plan;BatchRecording.Save(DraftPath,batchDraft);PublishBatch();
    }
    private object PickBatchFilms()
    {
        var dialog=new OpenFileDialog{Title="Add Theater recordings to your reel",Filter="MCC Theater recordings (*.mov)|*.mov",Multiselect=true,CheckFileExists=true};
        if(Directory.Exists(preferences.Movies))dialog.InitialDirectory=preferences.Movies;
        return ImportBatchFilms(dialog.ShowDialog(this)==true?dialog.FileNames:Array.Empty<string>());
    }
    private object ImportBatchFilms(IEnumerable<string> paths)
    {
        var selected=new List<object>();var errors=new List<string>();
        foreach(var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if(HighlightRunning)throw new IOException("Finish the highlight job before opening playback.");
            var film=ReadRecording(path);selected.Add(new{film.Id,film.Name,film.Map,film.Path,film.TotalTicks,film.Bytes,film.Modified,film.CanDecode,film.Reason,film.Duration,film.GameType,film.GameVariantName,film.Title,film.TitleName,film.ArtworkKey});
            }
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(path)}: {error.Message}");
            }
        }
        return new{films=selected,errors};
    }

    private FrameworkElement CreatePlayback(Recording film,NativeSurface native)
    {
        var panel=new DockPanel {Background=Brushes.Black};
        var top=new DockPanel {Background=PanelBrush,Margin=new Thickness(0,1,0,0)};
        var heading=new StackPanel {Margin=new Thickness(22,17,18,17)};
        heading.Children.Add(new TextBlock {Text=film.Map.ToUpperInvariant()+"  /  NATIVE PLAYBACK",Foreground=Accent,FontSize=11,FontWeight=FontWeights.SemiBold});
        heading.Children.Add(new TextBlock {Text=film.Name,Foreground=Brushes.White,FontSize=20,Margin=new Thickness(0,5,0,0),TextTrimming=TextTrimming.CharacterEllipsis});
        DockPanel.SetDock(heading,Dock.Left);top.Children.Add(heading);
        var renderStatus=new StackPanel {VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(10)};
        renderDimensions=Text("Native Halo renderer \u00b7 Waiting for render dimensions\u2026",11);renderDimensions.Foreground=Accent;
        nativeState=Text("Loading original recording and map\u2026",12);nativeState.Margin=new Thickness(0,5,0,0);
        renderStatus.Children.Add(renderDimensions);renderStatus.Children.Add(nativeState);top.Children.Add(renderStatus);
        DockPanel.SetDock(top,Dock.Top);panel.Children.Add(top);
        var transport=new StackPanel {Background=PanelBrush,Margin=new Thickness(0,1,0,0)};
        progress=new ProgressBar {Minimum=0,Maximum=Math.Max(film.TotalTicks,1),Height=4,Foreground=Accent,Background=Brushes.Black,IsHitTestVisible=false,ToolTip="Native film progress. Seeking is not connected in this proof of concept."};transport.Children.Add(progress);
        var controls=new WrapPanel {Orientation=Orientation.Horizontal,Margin=new Thickness(18,12,18,12)};
        Action? togglePreviewSize=null;
        var previewSize=Button("Actual size",()=>{togglePreviewSize?.Invoke();return Task.CompletedTask;});
        previewSize.ToolTip="View the preview at 100% to avoid resizing text. Scroll to pan. Recordings already use 2560 \u00d7 1440.";
        play=Button("Pause",()=>{native.Command(new{command=paused?"play":"pause"});return Task.CompletedTask;}); play.IsEnabled=false;controls.Children.Add(play);
        controls.Children.Add(Button("Restart recording",async()=>await OpenRecording(film.Path)));
        controls.Children.Add(previewSize);
        orbit=Button("3D orbit (perspective)",()=>{if(orbitEnabled){native.Command(new{command="orbit",enabled=false});native.Command(new{command="watchPlayer",playerDatum=-1});}else native.Command(new{command="orbit",enabled=true});return Task.CompletedTask;});orbit.IsEnabled=false;orbit.ToolTip="Experimental perspective orbit in the native renderer. Orthographic overview remains under investigation.";controls.Children.Add(orbit);
        recordedCamera=Button("Recorded camera",()=>{native.Command(new{command="orbit",enabled=false});native.Command(new{command="watchPlayer",playerDatum=-1});return Task.CompletedTask;});recordedCamera.IsEnabled=false;controls.Children.Add(recordedCamera);
        zoomIn=Button("Zoom +",()=>{native.Command(new{command="orbitInput",dx=0,dy=0,wheel=120});return Task.CompletedTask;});zoomIn.IsEnabled=false;controls.Children.Add(zoomIn);
        zoomOut=Button("Zoom \u2212",()=>{native.Command(new{command="orbitInput",dx=0,dy=0,wheel=-120});return Task.CompletedTask;});zoomOut.IsEnabled=false;controls.Children.Add(zoomOut);
        cameraOptions=Button("Camera \u25be",()=>Task.CompletedTask);cameraOptions.IsEnabled=false;
        var cameraMenu=new ContextMenu();centerOrbit=new MenuItem {Header="Center orbit",ToolTip="Reset the perspective orbit to its map pivot. The pivot does not follow a player."};
        centerOrbit.Click+=(_,_)=>native.Command(new{command="orbitReset"});cameraMenu.Items.Add(centerOrbit);cameraOptions.ContextMenu=cameraMenu;
        cameraOptions.Click+=(_,_)=>{cameraMenu.PlacementTarget=cameraOptions;cameraMenu.IsOpen=true;};controls.Children.Add(cameraOptions);
        transportTime=Text("00:00 / "+film.Duration,13);transportTime.VerticalAlignment=VerticalAlignment.Center;transportTime.Margin=new Thickness(12,0,0,0);controls.Children.Add(transportTime);transport.Children.Add(controls);
        var captureControls=new WrapPanel {Margin=new Thickness(18,0,18,10)};
        showPlayerName=new CheckBox {Content="Show player name",IsChecked=preferences.ShowPlayerName,IsEnabled=false,Foreground=Brushes.LightGray,VerticalContentAlignment=VerticalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(3,0,18,0),ToolTip="Native player-name control becomes available after the scene loads."};
        showPlayerName.Click+=(_,_)=>{preferences.ShowPlayerName=showPlayerName.IsChecked==true;SavePreferences();lastPlayerNameRequest=null;ApplyPlayerNamePreference();UpdateCaptureControls();};captureControls.Children.Add(showPlayerName);
        nativeScoreboard=new CheckBox {Content="Native scoreboard",IsChecked=false,IsEnabled=false,Foreground=Brushes.LightGray,VerticalContentAlignment=VerticalAlignment.Center,VerticalAlignment=VerticalAlignment.Center,Margin=new Thickness(0,0,18,0),ToolTip="Show Halo's original scoreboard inside the rendered view and captured video. The analysis panel is separate."};
        nativeScoreboard.Click+=(_,_)=>{if(surface!=null&&scoreboardSupported&&NativePlaybackAvailable){var visible=nativeScoreboard.IsChecked==true;surface.Command(new{command="scoreboard",visible});Record("native-scoreboard-request",new{visible});}};captureControls.Children.Add(nativeScoreboard);
        recordMp4=Button("Record MP4\u2026",()=>{ChooseCaptureFile();return Task.CompletedTask;});recordMp4.IsEnabled=false;recordMp4.Background=Accent;recordMp4.Foreground=Brushes.Black;recordMp4.ToolTip="Capture native rendering and game audio from the native playback process only. Other apps, the analysis panel and app controls are excluded.";captureControls.Children.Add(recordMp4);
        stopRecording=Button("Stop recording",()=>{RequestStopRecording();return Task.CompletedTask;});stopRecording.IsEnabled=false;captureControls.Children.Add(stopRecording);
        var qualityHint=Text("YouTube HQ \u00b7 1440p60 \u00b7 50 Mbps target",11);qualityHint.VerticalAlignment=VerticalAlignment.Center;qualityHint.Margin=new Thickness(6,0,12,0);captureControls.Children.Add(qualityHint);
        recordingStatus=Text("Checking game-audio capture support\u2026 Native playback process only",11);recordingStatus.VerticalAlignment=VerticalAlignment.Center;recordingStatus.Margin=new Thickness(6,0,0,0);captureControls.Children.Add(recordingStatus);transport.Children.Add(captureControls);
        cameraHint=Text("Recorded camera \u00b7 Native playback",11);cameraHint.Margin=new Thickness(20,0,20,12);transport.Children.Add(cameraHint);
        DockPanel.SetDock(transport,Dock.Bottom);panel.Children.Add(transport);
        var middle=new Grid();middle.ColumnDefinitions.Add(new ColumnDefinition());middle.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(320)});
        var viewport=new Grid {Background=Brushes.Black};
        var previewScroll=new ScrollViewer {Content=viewport,Background=Brushes.Black,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,VerticalScrollBarVisibility=ScrollBarVisibility.Disabled,
            HorizontalContentAlignment=HorizontalAlignment.Center,VerticalContentAlignment=VerticalAlignment.Center,CanContentScroll=false,UseLayoutRounding=true};
        native.HorizontalAlignment=HorizontalAlignment.Center;native.VerticalAlignment=VerticalAlignment.Center;
        bool actualSize=false,centerPreview=false;
        Rect? appliedClip=null;
        void Fit()
        {
            if(actualSize)
            {
                var dpi=VisualTreeHelper.GetDpi(native);
                native.Width=viewport.Width=2560/dpi.DpiScaleX;
                native.Height=viewport.Height=1440/dpi.DpiScaleY;
            }
            else
            {
                viewport.Width=viewport.Height=double.NaN;
                double width=Math.Max(1,previewScroll.ActualWidth),height=Math.Max(1,previewScroll.ActualHeight);
                var scaled=Math.Min(width/16,height/9);
                native.Width=Math.Max(1,Math.Floor(scaled*16));native.Height=Math.Max(1,Math.Floor(scaled*9));
            }
        }
        void ClipPreview()
        {
            if(native.Handle==IntPtr.Zero||!native.IsVisible||
                previewScroll.Template?.FindName("PART_ScrollContentPresenter",previewScroll) is not FrameworkElement clipViewport)return;
            // HwndHost owns a real child HWND, which WPF's ScrollViewer clip
            // cannot crop. Clip its region while retaining the full render size.
            var origin=native.TranslatePoint(new Point(),clipViewport);
            var visible=new Rect(origin,new Size(native.ActualWidth,native.ActualHeight));
            visible.Intersect(new Rect(0,0,clipViewport.ActualWidth,clipViewport.ActualHeight));
            var dpi=VisualTreeHelper.GetDpi(native);
            int left=0,top=0,right=0,bottom=0;
            if(!visible.IsEmpty)
            {
                left=(int)Math.Ceiling((visible.Left-origin.X)*dpi.DpiScaleX);
                top=(int)Math.Ceiling((visible.Top-origin.Y)*dpi.DpiScaleY);
                right=(int)Math.Floor((visible.Right-origin.X)*dpi.DpiScaleX);
                bottom=(int)Math.Floor((visible.Bottom-origin.Y)*dpi.DpiScaleY);
            }
            var clip=new Rect(left,top,Math.Max(0,right-left),Math.Max(0,bottom-top));
            if(appliedClip==clip)return;
            var region=CreateRectRgn(left,top,Math.Max(left,right),Math.Max(top,bottom));
            if(region==IntPtr.Zero)return;
            // Windows owns the region only after a successful SetWindowRgn.
            if(SetWindowRgn(native.Handle,region,true)==0)DeleteObject(region);else appliedClip=clip;
        }
        togglePreviewSize=()=>
        {
            actualSize=!actualSize;centerPreview=actualSize;
            previewSize.Content=actualSize?"Fit view":"Actual size";
            previewScroll.HorizontalScrollBarVisibility=actualSize?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled;
            previewScroll.VerticalScrollBarVisibility=actualSize?ScrollBarVisibility.Auto:ScrollBarVisibility.Disabled;
            if(!actualSize){previewScroll.ScrollToHorizontalOffset(0);previewScroll.ScrollToVerticalOffset(0);}
            Fit();Record("preview-size",new{actualSize});
        };
        previewScroll.SizeChanged+=(_,_)=>Fit();
        native.DpiChanged+=(_,_)=>{Fit();appliedClip=null;};
        previewScroll.ScrollChanged+=(_,_)=>ClipPreview();
        native.LayoutUpdated+=(_,_)=>
        {
            if(centerPreview&&previewScroll.ViewportWidth>0&&previewScroll.ViewportHeight>0)
            {
                centerPreview=false;
                var dpi=VisualTreeHelper.GetDpi(native);
                previewScroll.ScrollToHorizontalOffset(Math.Round(previewScroll.ScrollableWidth*dpi.DpiScaleX/2)/dpi.DpiScaleX);
                previewScroll.ScrollToVerticalOffset(Math.Round(previewScroll.ScrollableHeight*dpi.DpiScaleY/2)/dpi.DpiScaleY);
            }
            ClipPreview();
        };
        viewport.Children.Add(native);middle.Children.Add(previewScroll);
        var details=new StackPanel {Margin=new Thickness(15,12,15,15)};
        details.Children.Add(new TextBlock {Text="RECORDING",Foreground=Accent,FontSize=11,FontWeight=FontWeights.SemiBold});
        details.Children.Add(new TextBlock {Text=film.GameVariantName??film.GameType??film.TitleName+" Theater",Foreground=Brushes.White,FontSize=18,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,15)});
        details.Children.Add(Text($"Map  {film.Map}\nDuration  {film.Duration}\nRecording  {film.Bytes/1048576.0:F1} MiB",12));
        details.Children.Add(new TextBlock {Text="NATIVE HOST",Foreground=Accent,FontSize=11,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,30,0,10)});
        metrics=Text("Waiting for native telemetry\u2026",12);details.Children.Add(metrics);
        var sidebarLayout=new DockPanel {Background=PanelBrush};
        var hostDetails=new Expander {Header="Recording and native host",Foreground=Brushes.LightGray,Background=PanelBrush,Content=details,Margin=new Thickness(10,0,10,10)};
        DockPanel.SetDock(hostDetails,Dock.Bottom);sidebarLayout.Children.Add(hostDetails);
        playersPane=new PlayersPane();playersPane.PlayerPovRequested+=datum=>{native.Command(new{command="orbit",enabled=false});native.Command(new{command="watchPlayer",playerDatum=datum});};sidebarLayout.Children.Add(playersPane);
        var sidebar=new Border {Background=PanelBrush,Child=sidebarLayout};Grid.SetColumn(sidebar,1);middle.Children.Add(sidebar);panel.Children.Add(middle);return panel;
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr value);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(IntPtr window,IntPtr region,bool redraw);

    private void HandleNativeEvent(JsonElement value)
    {
        if(!value.TryGetProperty("type",out var kind))return;
        string S(string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.String?item.GetString()??"":"";
        double N(string key)=>value.TryGetProperty(key,out var item)&&item.TryGetDouble(out var number)?number:0;
        bool B(string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.True;
        switch(kind.GetString())
        {
            case "viewSettings":activeFov.Text=$"Active FOV: {N("fovDegrees"):0}\u00b0 \u00b7 vehicle {N("vehicleFovDegrees"):0}\u00b0";Record("native-view-settings",value.Clone());break;
            case "error":HandleNativeFailure(S("message"));if(nativeState!=null)nativeState.Text=S("message");break;
            case "window":ConfirmRenderDimensions(value,"width","height");break;
            case "status":if(nativeState!=null)nativeState.Text=S("message");status.Text=S("message");Record("native-status",new{phase=S("phase"),message=S("message")});break;
            case "command":Record("native-command",value.Clone());if(S("command")=="watchPlayer"){watchCommandVersion++;watchCommandAccepted=value.TryGetProperty("ok",out var accepted)&&accepted.ValueKind is JsonValueKind.True or JsonValueKind.False?accepted.GetBoolean():null;}if(value.TryGetProperty("ok",out var ok)&&ok.ValueKind==JsonValueKind.False)ShowError(S("message"));break;
            case "ready":nativeSceneReady=true;Record("native-scene-ready",value.Clone());break;
            case "telemetry":
                telemetryCount++;lastTick=(int)N("tick");if(firstTick<0)firstTick=lastTick;
                paused=B("paused");orbitEnabled=B("orbitEnabled");
                if(value.TryGetProperty("sceneReady",out _))nativeSceneReady=B("sceneReady");
                ConfirmRenderDimensions(value,"renderWidth","renderHeight");
                nativeGameState=(int)N("gameState");playerPovSupported=B("playerPovSupported");
                playerNameVisibilitySupported=B("playerNameVisibilitySupported");
                if(value.TryGetProperty("playerNameVisible",out var visible)&&visible.ValueKind is JsonValueKind.True or JsonValueKind.False)playerNameVisible=visible.GetBoolean();
                if(value.TryGetProperty("audioCaptureSupported",out var audioSupported)&&audioSupported.ValueKind is JsonValueKind.True or JsonValueKind.False)audioCaptureSupported=audioSupported.GetBoolean();
                if(value.TryGetProperty("audioCaptureError",out var audioError)&&audioError.ValueKind==JsonValueKind.String)audioCaptureError=audioError.GetString()??"";
                scoreboardSupported=B("scoreboardSupported");scoreboardVisible=B("scoreboardVisible");
                if(nativeScoreboard!=null){nativeScoreboard.IsEnabled=scoreboardSupported&&NativePlaybackAvailable;nativeScoreboard.IsChecked=scoreboardVisible;nativeScoreboard.ToolTip=scoreboardSupported?"Show Halo's original scoreboard inside the rendered view and captured video. The analysis panel is separate.":"The native host has not confirmed scoreboard control support.";}
                ApplyPlayerNamePreference();UpdateCaptureControls();TryStartAutomaticRecording();
                watchedPlayerDatum=value.TryGetProperty("watchedPlayerDatum",out var watched)&&watched.ValueKind==JsonValueKind.Number&&watched.TryGetInt32(out var watchedDatum)?watchedDatum:-1;
                playersPane?.SetCameraCapability(playerPovSupported,watchedPlayerDatum,NativePlaybackAvailable);
                if(play!=null){play.Content=paused?"Play":"Pause";play.IsEnabled=NativePlaybackAvailable&&lastTick>0;}
                if(orbit!=null){orbit.IsEnabled=B("orbitSupported")&&NativePlaybackAvailable&&lastTick>0;orbit.Content=orbitEnabled?"Recorded camera":"3D orbit (perspective)";}
                if(zoomIn!=null)zoomIn.IsEnabled=orbitEnabled;if(zoomOut!=null)zoomOut.IsEnabled=orbitEnabled;
                if(cameraOptions!=null)cameraOptions.IsEnabled=orbitEnabled;
                if(recordedCamera!=null)recordedCamera.IsEnabled=NativePlaybackAvailable&&(orbitEnabled||watchedPlayerDatum!=-1);
                if(cameraHint!=null)cameraHint.Text=orbitEnabled?"Perspective orbit around a map pivot \u00b7 Drag to rotate \u00b7 Wheel to zoom \u00b7 Camera \u2192 Center orbit to reset \u00b7 Recorded camera to return":playerPovSupported&&watchedPlayerDatum!=-1?"Experimental native player first-person camera \u00b7 Recorded camera returns to the original film viewpoint":"Recorded camera \u00b7 Native playback";
                if(cameraHint!=null&&!playerPovSupported)cameraHint.Text+="\nAlternate-player first-person is under investigation. Recorded playback and 3D orbit are available.";
                var total=(int)N("totalTicks");if(total<=0)total=current?.TotalTicks??1;
                if(progress!=null){progress.Maximum=Math.Max(total,1);progress.Value=Math.Clamp(lastTick,0,total);}
                if(transportTime!=null)transportTime.Text=Time(lastTick)+" / "+Time(total)+$" \u00b7 {Math.Clamp(lastTick*100.0/Math.Max(1,total),0,100):F0}%";
                if(metrics!=null)metrics.Text=$"Tick  {lastTick:N0} / {total:N0}\nFrames  {N("frames"):N0}\nResident RAM  {N("workingSetBytes")/1048576:F0} MiB\nPrivate memory  {N("privateBytes")/1048576:F0} MiB\nState  {N("gameState"):N0}";
                if(nativeSceneReady&&lastTick>0){status.Text=$"{(paused?"Paused":"Native playback")} \u00b7 {current?.Name}";if(nativeState!=null)nativeState.Text="Original recording \u00b7 Native Halo renderer";}
                if(!exerciseStarted&&args.Contains("--exercise-native")&&!args.Contains("--exercise-players")&&childAttached&&NativePlaybackAvailable&&lastTick>=120&&surface!=null)
                {
                    exerciseStarted=true;_ = ExerciseNative(surface,B("orbitSupported"));
                }
                TryStartPlayersExercise();
                break;
            case "capture":Record("native-capture",value.Clone());break;
            case "players":playersPane?.ApplySnapshot(value);Record("native-players",value.Clone());TryStartPlayersExercise();break;
            case "profile":playersPane?.ApplyProfile(value);Record("native-profile",value.Clone());break;
            case "recording":HandleRecordingEvent(value);break;
        }
    }

    private bool NativePlaybackAvailable=>nativeSceneReady&&nativeGameState is 1 or 8 or 9;
    private bool IsRecordingActive=>recordingState is "starting" or "recording" or "stopping";
    private bool PlayerNamePreferenceApplied=>playerNameVisibilitySupported&&lastPlayerNameRequest==preferences.ShowPlayerName&&playerNameVisible==preferences.ShowPlayerName;
    private void ApplyPlayerNamePreference()
    {
        if(!NativePlaybackAvailable||!playerNameVisibilitySupported||surface==null)return;
        if(lastPlayerNameRequest!=preferences.ShowPlayerName)
        {
            lastPlayerNameRequest=preferences.ShowPlayerName;
            surface.Command(new{command="playerName",visible=preferences.ShowPlayerName});
            Record("player-name-request",new{visible=preferences.ShowPlayerName});
        }
    }
    private void UpdateCaptureControls()
    {
        if(showPlayerName!=null){showPlayerName.IsEnabled=NativePlaybackAvailable&&playerNameVisibilitySupported&&!IsRecordingActive;showPlayerName.ToolTip=playerNameVisibilitySupported?"Show or hide the native player name in the rendered video.":"The native host has not confirmed player-name visibility support.";}
        if(recordMp4!=null){recordMp4.IsEnabled=NativePlaybackAvailable&&PlayerNamePreferenceApplied&&audioCaptureSupported==true&&!IsRecordingActive;recordMp4.ToolTip=audioCaptureSupported==false?"Game-audio capture is required. "+AudioCaptureFailure:"Capture native rendering and game audio from the native playback process only. Other apps, the analysis panel and app controls are excluded.";}
        if(stopRecording!=null)stopRecording.IsEnabled=recordingState is "starting" or "recording";
        if(recordingStatus!=null&&recordingState=="idle")recordingStatus.Text=audioCaptureSupported==true?GameAudioLabel:audioCaptureSupported==false?"Game-audio capture unavailable \u00b7 "+AudioCaptureFailure:"Checking game-audio capture support\u2026 Native playback process only";
    }
    private string AudioCaptureFailure=>string.IsNullOrWhiteSpace(audioCaptureError)?"The native host has not confirmed support for recording its own game audio.":audioCaptureError;
    private void ChooseCaptureFile()
    {
        if(surface==null||!NativePlaybackAvailable||!PlayerNamePreferenceApplied)throw new IOException("Wait for the scene and requested player-name visibility to be ready.");
        if(audioCaptureSupported!=true)throw new IOException("Game-audio capture is required. "+AudioCaptureFailure);
        var folder=Path.Combine(dataRoot,"Captures");Directory.CreateDirectory(folder);
        var name=Path.GetFileNameWithoutExtension(current?.Name??"Halo3");foreach(var character in Path.GetInvalidFileNameChars())name=name.Replace(character,'_');
        var dialog=new SaveFileDialog {Title="Record MP4 with game audio \u00b7 Native playback process only",Filter="MPEG-4 video (*.mp4)|*.mp4",DefaultExt=".mp4",AddExtension=true,OverwritePrompt=false,InitialDirectory=folder,FileName=name+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".mp4"};
        if(dialog.ShowDialog(this)!=true)return;
        StartRecording(dialog.FileName);
    }
    private void StartRecording(string path)
    {
        if(surface==null||IsRecordingActive)throw new IOException("A recording is already active or its native host is unavailable.");
        if(audioCaptureSupported!=true)throw new IOException("Game-audio capture is required. "+AudioCaptureFailure);
        path=Path.GetFullPath(path);if(!path.EndsWith(".mp4",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose an .mp4 output file.");
        if(File.Exists(path))throw new IOException("That video already exists. Choose a new filename; existing captures are kept.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        recordingFile=path;recordingState="starting";recordingDuration=0;recordingAudio=null;requiredAudioFailure="";
        if(recordingStatus!=null){recordingStatus.Text="Starting MP4 \u00b7 "+GameAudioLabel+" \u00b7 "+Path.GetFileName(path);recordingStatus.ToolTip=path;}
        Record("record-start-request",new{file=path,audio=true,playerNameVisible});surface.Command(new{command="recordStart",file=path,audio=true});UpdateCaptureControls();
    }
    private void RequestStopRecording()
    {
        if(surface==null||recordingState is not("starting" or "recording"))return;
        recordingState="stopping";surface.Command(new{command="recordStop"});Record("record-stop-request",new{file=recordingFile});
        if(recordingStatus!=null)recordingStatus.Text="Finalizing MP4\u2026 "+GameAudioLabel;UpdateCaptureControls();
    }
    private void TryStartAutomaticRecording()
    {
        if(closing||switching||autoRecordStarted||Argument("--record-output") is not string path||!NativePlaybackAvailable||!PlayerNamePreferenceApplied)return;
        if(audioCaptureSupported==null)return;
        autoRecordStarted=true;
        try{surface?.Command(new{command="orbit",enabled=false});surface?.Command(new{command="watchPlayer",playerDatum=-1});StartRecording(path);}
        catch(Exception error){recordingState="error";ShowError(error.Message);if(recordingStatus!=null)recordingStatus.Text="Recording error \u00b7 "+error.Message;UpdateCaptureControls();FinishRecordingAutomation(true);}
    }
    private void HandleRecordingEvent(JsonElement value)
    {
        string Read(string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.String?item.GetString()??"":"";
        double Number(string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.Number&&item.TryGetDouble(out var number)?number:0;
        recordingState=Read("state");if(Read("file") is string file&&file.Length>0)recordingFile=file;recordingDuration=Number("durationSeconds");recordingFrames=(long)Number("frames");recordingDroppedFrames=(long)Number("droppedFrames");
        if(value.TryGetProperty("audio",out var audio)&&audio.ValueKind is JsonValueKind.True or JsonValueKind.False)recordingAudio=audio.GetBoolean();
        Record("native-recording",value.Clone());
        if(recordingState is "starting" or "recording"&&recordingAudio==false)
        {
            requiredAudioFailure="The native export reported no game audio. A silent MP4 does not satisfy this capture request.";ShowError(requiredAudioFailure);RequestStopRecording();
        }
        if(recordingState=="stopped"&&(recordingAudio!=true||requiredAudioFailure.Length>0))
        {
            requiredAudioFailure=requiredAudioFailure.Length>0?requiredAudioFailure:"The finalized MP4 did not confirm its required game-audio track.";recordingState="error";ShowError(requiredAudioFailure);
        }
        if(recordingStatus!=null)
        {
            recordingStatus.ToolTip=recordingFile;
            recordingStatus.Text=recordingState switch
            {
                "recording"=>$"Recording {recordingDuration:F1}s \u00b7 {Number("frames"):N0} frames \u00b7 {Number("droppedFrames"):N0} dropped \u00b7 {GameAudioLabel}",
                "starting"=>"Starting MP4 \u00b7 "+GameAudioLabel,
                "stopping"=>requiredAudioFailure.Length>0?"Recording failed: "+requiredAudioFailure:"Finalizing MP4\u2026 "+GameAudioLabel,
                "stopped"=>$"Saved {Path.GetFileName(recordingFile)} \u00b7 {recordingDuration:F1}s \u00b7 {GameAudioLabel}",
                "error"=>"Recording error \u00b7 "+(requiredAudioFailure.Length>0?requiredAudioFailure:Read("error")),
                _=>GameAudioLabel
            };
        }
        if(recordingState=="error")ShowError(requiredAudioFailure.Length>0?requiredAudioFailure:Read("error"));
        if(recordingState=="recording"&&Argument("--record-output")!=null&&double.TryParse(Argument("--record-seconds"),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out var seconds)&&seconds>0&&recordingDuration>=seconds)RequestStopRecording();
        UpdateCaptureControls();
        if(recordingState is "stopped" or "error")FinishRecordingAutomation(recordingState=="error");
    }

    private void FinishRecordingAutomation(bool failed)
    {
        if(!bool.TryParse(Argument("--exit-after-record"),out var exitAfterRecord)||!exitAfterRecord)return;
        if(failed)Environment.ExitCode=1;
        Dispatcher.BeginInvoke(()=>{if(!closing)Close();});
    }

    private void ConfirmRenderDimensions(JsonElement value,string widthKey,string heightKey)
    {
        if(!value.TryGetProperty(widthKey,out var widthValue)||widthValue.ValueKind!=JsonValueKind.Number||!widthValue.TryGetInt32(out var width)||
           !value.TryGetProperty(heightKey,out var heightValue)||heightValue.ValueKind!=JsonValueKind.Number||!heightValue.TryGetInt32(out var height)||
           width<=0||height<=0||width>16384||height>16384||(renderWidth==width&&renderHeight==height))return;
        renderWidth=width;renderHeight=height;
        if(renderDimensions!=null)renderDimensions.Text=$"{width} \u00d7 {height} \u00b7 Native Halo renderer";
        Record("native-render-dimensions",new{width,height,source=value.GetProperty("type").GetString()});
    }

    private void TryStartPlayersExercise()
    {
        if(playersExerciseStarted||!args.Contains("--exercise-players")||!childAttached||!NativePlaybackAvailable||lastTick<120||!playerPovSupported||surface==null||playersPane==null)return;
        var live=playersPane.Players.Where(p=>p.Active==true&&p.CanWatch).GroupBy(p=>p.PlayerDatum).Select(g=>g.First()).OrderBy(p=>p.Index).ToArray();
        if(live.Length<2)return;
        playersExerciseStarted=true;_ = ExercisePlayers(surface,live);
    }

    // Separate opt-in proof for recorded identities, native score snapshots and
    // experimental player POV. Captures are evidence, not a claim that a remote
    // player's original aim can be reproduced when the film omitted its input.
    private async Task ExercisePlayers(NativeSurface owned,IReadOnlyList<PlayerSnapshot> live)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));var token=timeout.Token;
        void Check(){token.ThrowIfCancellationRequested();if(closing||surface!=owned)throw new OperationCanceledException("The test session closed or changed.");}
        void Send(object command,string step){Check();Record("exercise-players-step",new{step,tick=lastTick,paused,watchedPlayerDatum});owned.Command(command);}
        async Task Delay(int milliseconds){await Task.Delay(milliseconds,token);Check();}
        void Capture(string label)=>Send(new{command="capture",label},label);
        async Task Watch(int datum,string step)
        {
            int version=watchCommandVersion;Send(new{command="watchPlayer",playerDatum=datum},step);await Delay(1000);
            Record("exercise-players-pov-command",new{step,requestedPlayerDatum=datum,reportedWatchedPlayerDatum=watchedPlayerDatum,acknowledged=watchCommandVersion>version,commandAccepted=watchCommandVersion>version?watchCommandAccepted:null,tick=lastTick});
        }
        try
        {
            Check();var candidates=live.OrderBy(p=>p.PlayerDatum==watchedPlayerDatum?1:0).ThenBy(p=>p.IsLocal==true?1:0).ThenBy(p=>p.Index).ToArray();var first=candidates[0];var second=candidates.First(p=>p.PlayerDatum!=first.PlayerDatum);
            var roster=playersPane?.Players??Array.Empty<PlayerSnapshot>();
            Record("exercise-players-start",new{tick=lastTick,source=playersPane?.Source,statsAvailable=playersPane?.StatsAvailable??false,
                playersWithVerifiedStats=roster.Count(p=>p.Kills.HasValue||p.Deaths.HasValue||p.Assists.HasValue||p.Score.HasValue),players=roster,
                firstPlayer=first.Name,firstDatum=first.PlayerDatum,secondPlayer=second.Name,secondDatum=second.PlayerDatum,playerPovSupported});
            Send(new{command="pause"},"pause");for(int attempt=0;attempt<30&&!paused;attempt++)await Delay(100);
            if(!paused)throw new IOException("The player integration exercise did not receive paused telemetry.");
            Send(new{command="orbit",enabled=false},"orbit-off");Capture("exercise-players-recorded-baseline");await Delay(1000);
            await Watch(first.PlayerDatum!.Value,"watch-first-player");Capture("exercise-players-first-pov");await Delay(1000);
            await Watch(second.PlayerDatum!.Value,"watch-second-player");Capture("exercise-players-second-pov");await Delay(1000);
            int liveBaseline=lastTick;Send(new{command="play"},"play-second-player");await Delay(3000);Capture("exercise-players-second-pov-live");
            Record("exercise-players-live-validation",new{baselineTick=liveBaseline,observedTick=lastTick,advanced=lastTick>liveBaseline,watchedPlayerDatum});
            await Delay(250);Send(new{command="pause"},"pause-second-player");await Delay(1000);Capture("exercise-players-second-pov-paused");await Delay(250);
            await Watch(-1,"restore-recorded-camera");Capture("exercise-players-recorded-restored");await Delay(1000);
            Send(new{command="play"},"resume");var resumeBaseline=lastTick;for(int attempt=0;attempt<30&&lastTick<=resumeBaseline;attempt++)await Delay(100);
            Record("exercise-players-complete",new{resumeBaselineTick=resumeBaseline,observedTick=lastTick,resumed=lastTick>resumeBaseline,watchedPlayerDatum,
                source=playersPane?.Source,statsAvailable=playersPane?.StatsAvailable??false,players=playersPane?.Players,
                remoteAimFidelity="Not established by this exercise; remote inputs may be absent from the film."});
        }
        catch(OperationCanceledException){Record("exercise-players-cancelled",new{reason=timeout.IsCancellationRequested?"40 second timeout":"session changed"});if(surface==owned&&!closing){owned.Command(new{command="watchPlayer",playerDatum=-1});owned.Command(new{command="orbit",enabled=false});owned.Command(new{command="play"});}}
        catch(Exception error){Record("exercise-players-failed",new{message=error.Message});if(surface==owned&&!closing){owned.Command(new{command="watchPlayer",playerDatum=-1});owned.Command(new{command="orbit",enabled=false});owned.Command(new{command="play"});}}
    }

    // Opt-in integration exercise for the native capture preview. It never runs in normal use.
    private async Task ExerciseNative(NativeSurface owned,bool orbitSupported)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var token=timeout.Token;
        void Check(){token.ThrowIfCancellationRequested();if(closing||surface!=owned)throw new OperationCanceledException("The test session closed or changed.");}
        void Send(object command,string step){Check();Record("exercise-step",new{step,tick=lastTick,paused,orbitEnabled});owned.Command(command);}
        async Task Delay(int milliseconds){await Task.Delay(milliseconds,token);Check();}
        void Capture(string label)=>Send(new{command="capture",label},label);
        try
        {
            Check();Record("exercise-start",new{tick=lastTick,orbitSupported});
            Send(new{command="pause"},"pause");
            for(int attempt=0;attempt<30&&!paused;attempt++)await Delay(100);
            if(!paused)throw new IOException("The integration exercise did not receive paused telemetry.");
            var pauseBaseline=lastTick;Capture("exercise-recorded-baseline");await Delay(1000);
            Record("exercise-pause-validation",new{baselineTick=pauseBaseline,observedTick=lastTick,passed=paused&&lastTick==pauseBaseline});
            if(!orbitSupported)throw new IOException("The native host did not report orbit support.");
            Send(new{command="orbit",enabled=true},"orbit-on");await Delay(1000);Capture("exercise-orbit-initial");
            Send(new{command="orbitInput",dx=80,dy=25,wheel=0},"orbit-rotate");await Delay(1000);Capture("exercise-orbit-rotated");
            Send(new{command="orbitInput",dx=0,dy=0,wheel=240},"orbit-zoom");await Delay(1000);Capture("exercise-orbit-zoomed");
            Send(new{command="orbit",enabled=false},"orbit-off");await Delay(1000);Capture("exercise-recorded-restored");
            await Delay(1000);Send(new{command="play"},"play");var resumeBaseline=lastTick;
            for(int attempt=0;attempt<30&&lastTick<=resumeBaseline;attempt++)await Delay(100);
            Record("exercise-complete",new{resumeBaselineTick=resumeBaseline,observedTick=lastTick,resumed=lastTick>resumeBaseline,paused,orbitEnabled});
        }
        catch(OperationCanceledException){Record("exercise-cancelled",new{reason=timeout.IsCancellationRequested?"40 second timeout":"session changed"});if(surface==owned&&!closing){owned.Command(new{command="orbit",enabled=false});owned.Command(new{command="play"});}}
        catch(Exception error){Record("exercise-failed",new{message=error.Message});if(surface==owned&&!closing){owned.Command(new{command="orbit",enabled=false});owned.Command(new{command="play"});}}
    }
    private static string Time(int ticks){var seconds=Math.Max(0,ticks)/60;return seconds>=3600?$"{seconds/3600}:{seconds/60%60:00}:{seconds%60:00}":$"{seconds/60:00}:{seconds%60:00}";}

    private Recording ReadRecording(string file)
    {
        var film=ReadFilm(file,preferences.Maps);recordings[film.Id]=film;return film;
    }
    internal static Recording ReadFilm(string file,string maps)
    {
        file=Path.GetFullPath(file);var info=new FileInfo(file);
        if(!info.Exists||!file.EndsWith(".mov",StringComparison.OrdinalIgnoreCase))throw new IOException("Choose an original MCC .mov recording.");
        var metadata=FilmMetadata.Read(file)??throw new IOException("This file does not have a supported Halo 3, Reach, Halo 4 or H2A film header.");
        var title=metadata.Title;var map=metadata.Map;
        string? reason=string.IsNullOrWhiteSpace(maps)?"Choose the installed MCC game folder.":title.InstallationIssue(maps);
        if(reason==null&&!File.Exists(Path.Combine(title.MapsFolder(maps),map+".map")))
            reason=$"Matching original {title.DisplayName} map missing: {map}.map.";
        var ticks=metadata.TotalTicks;var id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(file.ToLowerInvariant())))[..20].ToLowerInvariant();
        return new Recording(id,info.Name,map,info.Length,info.LastWriteTimeUtc.ToString("O"),reason==null,reason,metadata.GameType,metadata.GameVariantName,Time(ticks),ticks,file)
        {Title=title.Id,TitleName=title.DisplayName,ArtworkKey=title==FilmTitle.Halo3?map.ToUpperInvariant():title.Id+":"+map.ToUpperInvariant()};
    }
    private object Catalog()
    {
        var files=new List<string>(preferences.Recent.Where(File.Exists));
        if(Directory.Exists(preferences.Movies))
        {
            files.AddRange(Directory.EnumerateFiles(preferences.Movies,"*.mov"));
            if(Path.GetFileName(preferences.Movies.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar)).Equals("UserContent",StringComparison.OrdinalIgnoreCase))
                foreach(var title in FilmTitle.All)
                {
                    var folder=Path.Combine(preferences.Movies,title.MovieFolderName,"Movie");
                    if(Directory.Exists(folder))files.AddRange(Directory.EnumerateFiles(folder,"*.mov"));
                }
        }
        files.AddRange(batchDraft.Items.Select(item=>item.Film).Where(File.Exists));
        var films=new List<Recording>();foreach(var path in files.Distinct(StringComparer.OrdinalIgnoreCase)){try{films.Add(ReadRecording(path));}catch(IOException){}catch(UnauthorizedAccessException){}}
        if(artworkMaps!=preferences.Maps)
        {
            artworkMaps=preferences.Maps;
            try { artwork=MapArtwork.Load(preferences.Maps,Path.Combine(dataRoot,"MapArtwork")); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            { artwork=new();Record("map-artwork-unavailable",new{message=error.Message}); }
        }
        return new{movies=preferences.Movies,maps=preferences.Maps,artwork,films=films.OrderByDescending(f=>f.Modified).Select(f=>new{f.Id,f.Name,f.Map,f.Path,f.TotalTicks,f.Bytes,f.Modified,f.CanDecode,f.Reason,f.GameType,f.GameVariantName,f.Duration,f.Title,f.TitleName,f.ArtworkKey}),desktop=true,backgroundRecording=preferences.BackgroundRecording,job=backgroundStatus,batch=BatchState(),highlights=HighlightsState(),monitoring=MonitoringState()};
    }
    private string? PickFilm()
    {
        var dialog=new OpenFileDialog {Title="Choose an original MCC Theater recording",Filter="MCC Theater recordings (*.mov)|*.mov",CheckFileExists=true,Multiselect=false};if(Directory.Exists(preferences.Movies))dialog.InitialDirectory=preferences.Movies;
        if(dialog.ShowDialog(this)!=true)return null;preferences.Movies=Path.GetDirectoryName(dialog.FileName)!;SavePreferences();return dialog.FileName;
    }
    private void PickFolder(bool maps)
    {
        var dialog=new OpenFolderDialog {Title=maps?"Choose the MCC installation or a title's maps folder":"Choose a Theater recordings folder",Multiselect=false};var path=maps?preferences.Maps:preferences.Movies;if(Directory.Exists(path))dialog.InitialDirectory=path;
        if(dialog.ShowDialog(this)!=true)return;if(maps)preferences.Maps=dialog.FolderName;else preferences.Movies=dialog.FolderName;SavePreferences();
    }
    private void LoadPreferences()
    {
        var path=Path.Combine(dataRoot,"settings.json");
        try{if(File.Exists(path))preferences=JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path),Json)??new();preferences.Recent??=new();}catch(Exception error)when(error is IOException or UnauthorizedAccessException or JsonException){preferences=new();}
    }
    private void SavePreferences(){try{File.WriteAllText(Path.Combine(dataRoot,"settings.json"),JsonSerializer.Serialize(preferences,Json));}catch(IOException){}}
    internal static string? FindMaps()
    {
        var steamRoots=new HashSet<string>(StringComparer.OrdinalIgnoreCase){Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),"Steam")};
        foreach(var key in new[]{Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam","SteamPath",null),Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam","InstallPath",null)})if(key is string root)steamRoots.Add(root);
        foreach(var root in steamRoots.ToArray())
        {
            var vdf=Path.Combine(root,"steamapps","libraryfolders.vdf");
            try{if(File.Exists(vdf))foreach(Match match in Regex.Matches(File.ReadAllText(vdf),"\"path\"\\s+\"([^\"]+)\""))steamRoots.Add(match.Groups[1].Value.Replace("\\\\","\\"));}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        }
        var candidates=steamRoots.Select(root=>Path.Combine(root,"steamapps","common","Halo The Master Chief Collection")).ToList();
        // Flat-file Xbox installations place DLC package metadata alongside the
        // combined content directory. Verify real engine/maps/shared assets so
        // that an entitlement-only Halo 3 package is never selected as a root.
        foreach(var drive in DriveInfo.GetDrives().Where(d=>d.DriveType==DriveType.Fixed))
        {
            string xbox=Path.Combine(drive.RootDirectory.FullName,"XboxGames");
            try{if(Directory.Exists(xbox))candidates.AddRange(Directory.EnumerateDirectories(xbox).Select(folder=>Path.Combine(folder,"Content")));}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException){}
        }
        foreach(var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            foreach(var title in FilmTitle.All)
                if(title.InstallationIssue(candidate)==null)return Path.Combine(candidate,title.Id,"maps");
        return null;
    }
    private void WriteReport()
    {
        if(Argument("--report") is not string path)return;
        try{path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(new{title=Title,executable=Environment.ProcessPath,dataRoot,nativeHost,film=current?.Path,childAttached,firstTick,lastTick,telemetryCount,elapsedSeconds=clock.Elapsed.TotalSeconds,
            capture=new{state=recordingState,file=recordingFile,durationSeconds=recordingDuration,frames=recordingFrames,droppedFrames=recordingDroppedFrames,audio=recordingAudio,audioCaptureSupported,audioCaptureError,requiredAudioFailure,playerNameVisible,requestedPlayerNameVisible=preferences.ShowPlayerName,scoreboardSupported,scoreboardVisible},backgroundRecording=backgroundRecording?.Status??backgroundStatus,events=diagnosticEvents},new JsonSerializerOptions(Json){WriteIndented=true}));}catch(IOException){}
    }
}
