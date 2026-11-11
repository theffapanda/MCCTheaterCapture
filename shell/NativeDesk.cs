#if NATIVE_UI_BETA
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace MCCTheaterCapture;

internal sealed partial class ViewerWindow
{
    private readonly TabControl nativeTabs = new();
    private readonly DataGrid nativeLibrary = NativeGrid(), nativeSequence = NativeGrid(), nativeClips = NativeGrid();
    private readonly TextBox nativeSearch = new();
    private readonly TextBlock nativeMessage = Text("Add original Theater films to begin.", 13);
    private readonly TextBlock nativeDestination = Text("", 12), nativeScanMessage = Text("", 12), nativeJobMessage = Text("No exports yet.", 13);
    private readonly ProgressBar nativeJobProgress = new() { Minimum=0, Maximum=1, Height=8, Margin=new Thickness(0,8,0,12) };
    private readonly Dictionary<string, TextBox> nativeInputs = new();
    private readonly Dictionary<string, ComboBox> nativeChoices = new();
    private readonly List<Control> nativeEditControls = new();
    private readonly CheckBox nativeContinue = new() { Content="Continue if a film fails", Foreground=Brushes.LightGray, IsChecked=true, Margin=new Thickness(0,8,0,10) };
    private readonly CheckBox nativeExact = new() { Content="Exact kill count only", Foreground=Brushes.LightGray, Margin=new Thickness(0,6,0,10) };
    private readonly Canvas nativeComposition = new() { Width=216, Height=384, Background=Brushes.Black, ClipToBounds=true };
    private readonly Image nativeArtwork = new() { Height=135, Stretch=Stretch.UniformToFill, Margin=new Thickness(0,0,0,10) };
    private StackPanel nativeSettings = new();
    private Button nativeStart = null!;
    private BatchPlan? nativeRetryBatch;
    private bool nativeLoading;
    private int nativeDragIndex = -1;
    private Point nativeDragStart;

    private static DataGrid NativeGrid() => new()
    {
        AutoGenerateColumns=false, IsReadOnly=true, CanUserAddRows=false, CanUserDeleteRows=false,
        SelectionMode=DataGridSelectionMode.Extended, SelectionUnit=DataGridSelectionUnit.FullRow,
        EnableRowVirtualization=true, EnableColumnVirtualization=true, HeadersVisibility=DataGridHeadersVisibility.Column,
        RowHeight=34, Margin=new Thickness(0,8,0,8), Background=Brushes.White, Foreground=Brushes.Black,
        GridLinesVisibility=DataGridGridLinesVisibility.Horizontal
    };
    private static void NativeColumn(DataGrid grid, string label, string path, double width=1, bool editable=false)
        => grid.Columns.Add(new DataGridTextColumn { Header=label, Binding=new Binding(path) { Mode=editable ? BindingMode.TwoWay : BindingMode.OneWay, UpdateSourceTrigger=UpdateSourceTrigger.LostFocus }, IsReadOnly=!editable, Width=new DataGridLength(width,DataGridLengthUnitType.Star) });
    private Button NativeAction(string label, Func<Task> action, bool editing=false)
    {
        var button = new Button { Content=label, Padding=new Thickness(12,7,12,7), Margin=new Thickness(0,0,7,7), MinHeight=34 };
        AutomationProperties.SetName(button,label);
        button.Click += async (_,_) => { try { await action(); } catch(Exception error) { NativeError(error); } };
        if(editing) nativeEditControls.Add(button);
        return button;
    }
    private Button NativeAction(string label, Action action, bool editing=false) => NativeAction(label,()=>{action();return Task.CompletedTask;},editing);
    private static WrapPanel NativeBar(params UIElement[] controls)
    {
        var bar=new WrapPanel { Margin=new Thickness(0,8,0,0) }; foreach(var control in controls)bar.Children.Add(control); return bar;
    }
    private static DockPanel NativePage(UIElement bar, UIElement content, UIElement? footer=null)
    {
        var page=new DockPanel { Margin=new Thickness(14) }; DockPanel.SetDock(bar,Dock.Top);page.Children.Add(bar);
        if(footer!=null){DockPanel.SetDock(footer,Dock.Bottom);page.Children.Add(footer);}page.Children.Add(content);return page;
    }
    private void NativeError(Exception error) { nativeMessage.Text=error.Message; nativeMessage.Foreground=Brushes.LightSalmon; ShowError(error.Message); }
    private bool NativeLocked => HighlightRunning || batchRecording?.IsRunning==true || backgroundRecording?.IsRunning==true || IsRecordingActive;
    private void NativeRequireEditable() { if(NativeLocked)throw new IOException("Finish or cancel the current scan/export before editing the sequence."); }

    private void BuildNativeDesk()
    {
        nativeLoading=true;
        nativeTabs.Background=new SolidColorBrush(Color.FromRgb(24,32,43));
        var root=new DockPanel { Background=new SolidColorBrush(Color.FromRgb(18,24,33)), LastChildFill=true, Margin=new Thickness(8) };
        var heading=new StackPanel { Margin=new Thickness(15,10,15,4) };
        heading.Children.Add(Text("MCC THEATER CAPTURE  /  NATIVE UI BETA",23));
        heading.Children.Add(Text("Original Theater films · Game audio · Standalone Windows x64",12));
        var toolbar=NativeBar(
            NativeAction("Add films…",NativePickFilms,true),
            NativeAction("New reel",NativeNewReel,true),
            NativeAction("Load job…",NativeLoadJob,true),
            NativeAction("Save job…",()=>NativeSaveJob(false),true),
            NativeAction("Theater folder…",()=>{NativeRequireEditable();PickFolder(false);RefreshNativeLibrary();},true),
            NativeAction("MCC folder…",()=>{NativeRequireEditable();PickFolder(true);RefreshNativeLibrary();},true));
        heading.Children.Add(toolbar);heading.Children.Add(nativeMessage);DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);
        var workspace=new Grid();workspace.ColumnDefinitions.Add(new ColumnDefinition());workspace.ColumnDefinitions.Add(new ColumnDefinition {Width=new GridLength(310)});
        workspace.Children.Add(nativeTabs);
        nativeSettings=new StackPanel {Margin=new Thickness(16,8,12,12)};
        nativeSettings.Children.Add(Text("EXPORT SETTINGS",17));
        NativeInput("name","Reel name",batchDraft.Name);
        NativeChoice("mode","Delivery",new[]{("separate","Separate files"),("combined","Combined video"),("both","Both")});
        NativeChoice("kind","Capture length",new[]{("full","Full films"),("sample","Quick samples"),("highlights","Multikill highlights")});
        NativeInput("seconds","Sample seconds (1–43200)","10");
        NativeChoice("container","File format",new[]{("mp4","MP4"),("mov","MOV / QuickTime")});
        NativeChoice("resolution","Resolution",new[]{("720","720p"),("1080","1080p"),("1440","1440p / native"),("2160","4K / upscaled")});
        NativeChoice("quality","Quality",new[]{("high","High (CRF 16; original at 1440p)"),("standard","Standard (CRF 20)"),("maximum","Maximum (CRF 12)"),("custom","Custom CRF")});
        NativeInput("crf","Custom CRF (0–51, lower = better)","18");
        NativeChoice("preset","Encoder speed (re-encodes)",ExportEncoding.Presets.Select(p=>(p,p=="ultrafast"?p+" (largest files)":p=="veryslow"?p+" (smallest files)":p=="fast"?p+" (default)":p)).ToArray());
        NativeChoice("tune","Encoder tune",ExportEncoding.Tunes.Select(t=>(t,t=="none"?"None (default)":t)).ToArray());
        NativeChoice("bitrate","Capture bitrate (at 60 fps)",ExportEncoding.CaptureBitrates.Select(b=>(b.ToString(CultureInfo.InvariantCulture),b+" Mbps"+(b==50?" (default)":""))).ToArray());
        NativeChoice("keyframe","Capture keyframe interval",new[]{("0.5","0.5 s (default)"),("1","1 s"),("2","2 s")});
        NativeChoice("audio","Highlight audio bitrate",ExportEncoding.AudioBitrates.Select(b=>(b.ToString(CultureInfo.InvariantCulture),b+" kbps"+(b==192?" (default)":""))).ToArray());
        NativeInput("fov","Field of view (55–120)","91");
        nativeSettings.Children.Add(nativeContinue);nativeEditControls.Add(nativeContinue);
        nativeContinue.Click+=(_,_)=>NativeEditChanged();
        nativeSettings.Children.Add(Text("Game audio included · 1440p + High keeps the original capture; other choices re-encode with x264",12));
        nativeSettings.Children.Add(NativeAction("Save folder…",NativePickDestination,true));nativeSettings.Children.Add(nativeDestination);
        nativeStart=NativeAction("Start export",()=>NativeStartExport(false));nativeSettings.Children.Add(nativeStart);
        nativeSettings.Children.Add(Text("Beta settings and jobs are kept in BetaUserData. Stable release data stays separate.",11));
        var settingsScroll=new ScrollViewer { Content=nativeSettings, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        Grid.SetColumn(settingsScroll,1);workspace.Children.Add(settingsScroll);root.Children.Add(workspace);

        NativeColumn(nativeLibrary,"Film","Name",2.5);NativeColumn(nativeLibrary,"Game","TitleName",1.4);NativeColumn(nativeLibrary,"Map","Map");
        NativeColumn(nativeLibrary,"Duration","Duration",.7);NativeColumn(nativeLibrary,"Availability","Reason",2.5);
        nativeSearch.Width=260;nativeSearch.Margin=new Thickness(0,0,8,7);nativeSearch.Padding=new Thickness(7);
        nativeSearch.ToolTip="Search films, maps, and game types";AutomationProperties.SetName(nativeSearch,"Search library");
        nativeSearch.TextChanged+=(_,_)=>NativeFilterLibrary();
        nativeLibrary.MouseDoubleClick+=async(_,_)=>{try{if(nativeLibrary.SelectedItem is Recording film)await OpenRecording(film.Path);}catch(Exception e){NativeError(e);}};
        nativeLibrary.SelectionChanged+=(_,_)=>NativeUpdateArtwork();
        var libraryFooter=new StackPanel();libraryFooter.Children.Add(nativeArtwork);libraryFooter.Children.Add(Text("Select films, then add them to the sequence. Double-click a film to open playback.",12));
        var directExport=new CheckBox{Content="Export playback directly to file",Foreground=Brushes.LightGray,IsChecked=preferences.BackgroundRecording,Margin=new Thickness(0,6,0,4)};
        directExport.Click+=(_,_)=>{preferences.BackgroundRecording=directExport.IsChecked==true;SavePreferences();};nativeEditControls.Add(directExport);libraryFooter.Children.Add(directExport);
        nativeTabs.Items.Add(new TabItem { Header="Library", Content=NativePage(NativeBar(nativeSearch,
            NativeAction("Refresh",RefreshNativeLibrary,true),NativeAction("Add selected →",()=>NativeAddFilms(nativeLibrary.SelectedItems.Cast<Recording>().ToArray()),true),
            NativeAction("Open playback",async()=>{if(nativeLibrary.SelectedItem is Recording film)await OpenRecording(film.Path);})),nativeLibrary,libraryFooter) });

        NativeColumn(nativeSequence,"Film","Name",2.4);NativeColumn(nativeSequence,"Game","Title");NativeColumn(nativeSequence,"Map","Map");NativeColumn(nativeSequence,"State","State");
        // Display order is export order; sorting only the grid would misrepresent it.
        nativeSequence.CanUserSortColumns=false;
        nativeSequence.AllowDrop=true;
        nativeSequence.PreviewMouseLeftButtonDown+=(_,e)=>{nativeDragStart=e.GetPosition(nativeSequence);};
        nativeSequence.MouseMove+=(_,e)=>{
            if(e.LeftButton!=MouseButtonState.Pressed||NativeLocked||nativeSequence.SelectedIndex<0)return;
            var point=e.GetPosition(nativeSequence);if(Math.Abs(point.X-nativeDragStart.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-nativeDragStart.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            nativeDragIndex=nativeSequence.SelectedIndex;DragDrop.DoDragDrop(nativeSequence,new DataObject("MCC.SequenceRow",nativeDragIndex),DragDropEffects.Move);nativeDragIndex=-1;
        };
        nativeSequence.Drop+=(_,e)=>{try{
            if(e.Data.GetDataPresent("MCC.SequenceRow") && e.OriginalSource is DependencyObject source){
                var row=ItemsControl.ContainerFromElement(nativeSequence,source) as DataGridRow;
                NativeMoveSequence(nativeDragIndex,row?.GetIndex()??batchDraft.Items.Count-1);e.Handled=true;
            }
        }catch(Exception error){NativeError(error);}};
        nativeTabs.Items.Add(new TabItem {Header="Sequence",Content=NativePage(NativeBar(
            NativeAction("Add films…",NativePickFilms,true),NativeAction("Move up",()=>NativeMoveSequence(nativeSequence.SelectedIndex,nativeSequence.SelectedIndex-1),true),
            NativeAction("Move down",()=>NativeMoveSequence(nativeSequence.SelectedIndex,nativeSequence.SelectedIndex+1),true),NativeAction("Remove selected",NativeRemoveFilms,true),
            NativeAction("Play selected",async()=>{if(nativeSequence.SelectedItem is BatchItem film)await OpenRecording(film.Film);})),nativeSequence,Text("Drag rows or use Move up / Move down to arrange the sequence.",12))});
        BuildNativeHighlights();
        BuildNativeMonitoring();
        nativeTabs.Items.Add(new TabItem {Header="Exports",Content=BuildNativeExports()});
        root.AllowDrop=true;root.DragOver+=(_,e)=>{if(e.Data.GetDataPresent(DataFormats.FileDrop)){e.Effects=NativeLocked?DragDropEffects.None:DragDropEffects.Copy;e.Handled=true;}};
        root.Drop+=(_,e)=>{if(!e.Data.GetDataPresent(DataFormats.FileDrop))return;try{NativeImport((string[])e.Data.GetData(DataFormats.FileDrop));e.Handled=true;}catch(Exception error){NativeError(error);}};
        browser.Children.Add(root);NativeApplyDraft();nativeLoading=false;RefreshNativeDesk();
    }

    private TextBox NativeInput(string key,string label,string value,StackPanel? parent=null)
    {
        parent??=nativeSettings;parent.Children.Add(Text(label,12));
        var input=new TextBox { Text=value, Padding=new Thickness(6), Margin=new Thickness(0,3,0,9), MinWidth=80 };
        AutomationProperties.SetName(input,label);nativeInputs[key]=input;nativeEditControls.Add(input);parent.Children.Add(input);
        input.TextChanged+=(_,_)=>NativeEditChanged();return input;
    }
    private ComboBox NativeChoice(string key,string label,(string,string)[] choices,StackPanel? parent=null)
    {
        parent??=nativeSettings;parent.Children.Add(Text(label,12));
        var control=new ComboBox { Margin=new Thickness(0,3,0,9), Padding=new Thickness(5), MinWidth=100 };
        foreach(var (value,title) in choices)control.Items.Add(new ComboBoxItem{Content=title,Tag=value});
        control.SelectedIndex=0;AutomationProperties.SetName(control,label);nativeChoices[key]=control;nativeEditControls.Add(control);parent.Children.Add(control);
        control.SelectionChanged+=(_,_)=>NativeEditChanged();return control;
    }
    private string NativeValue(string key) => (nativeChoices[key].SelectedItem as ComboBoxItem)?.Tag as string??"";
    private void NativeSet(string key,string value) { foreach(ComboBoxItem item in nativeChoices[key].Items)if((string)item.Tag==value){nativeChoices[key].SelectedItem=item;return;} }
    private int NativeInteger(string key,int min,int max) => int.TryParse(nativeInputs[key].Text,NumberStyles.Integer,CultureInfo.InvariantCulture,out var value)&&value>=min&&value<=max?value:throw new IOException($"{AutomationProperties.GetName(nativeInputs[key])}: enter {min}–{max}.");
    private double NativeNumber(string key,double min,double max) => double.TryParse(nativeInputs[key].Text,NumberStyles.Float,CultureInfo.InvariantCulture,out var value)&&double.IsFinite(value)&&value>=min&&value<=max?value:throw new IOException($"{AutomationProperties.GetName(nativeInputs[key])}: enter {min}–{max}.");

    private void NativeBeginEdit()
    {
        NativeRequireEditable();
        if(batchDraft.Signature.Length>0){nativeRetryBatch=JsonSerializer.Deserialize<BatchPlan>(JsonSerializer.Serialize(batchDraft,Json),Json)!;batchDraft=NativeBetaDraft.Fork(batchDraft);}
    }
    private void NativeReadDraft()
    {
        // Parse before mutating or forking, so partially entered numbers cannot corrupt a job.
        int fov=NativeInteger("fov",55,120), minimum=NativeInteger("minimum",2,10), crf=NativeInteger("crf",0,51);
        var encoding=new ExportEncoding{Crf=crf,Preset=NativeValue("preset"),Tune=NativeValue("tune"),CaptureBitrateMbps=int.Parse(NativeValue("bitrate"),CultureInfo.InvariantCulture),
            KeyframeSeconds=double.Parse(NativeValue("keyframe"),CultureInfo.InvariantCulture),AudioBitrateKbps=int.Parse(NativeValue("audio"),CultureInfo.InvariantCulture)};
        encoding.Validate();
        double before=NativeNumber("before",0,30),after=NativeNumber("after",0,30);
        double? seconds=NativeValue("kind")=="sample"?NativeNumber("seconds",1,43200):null;
        var format=new HighlightFormat {Layout=NativeValue("layout"),Blur=NativeInteger("blur",0,40),Dim=NativeInteger("dim",0,80),GameplayPosition=NativeInteger("position",30,65)};
        format.Validate();NativeBeginEdit();
        batchDraft.Name=nativeInputs["name"].Text;batchDraft.Mode=NativeValue("mode");batchDraft.OutputFormat=NativeValue("container");
        batchDraft.OutputResolution=int.Parse(NativeValue("resolution"),CultureInfo.InvariantCulture);batchDraft.OutputQuality=NativeValue("quality");batchDraft.Encoding=encoding;
        batchDraft.Fov=fov;batchDraft.CaptureKind=NativeValue("kind");batchDraft.SampleSeconds=seconds;batchDraft.ContinueOnError=nativeContinue.IsChecked==true;
        batchDraft.HighlightOptions=new(){MinimumKills=minimum,BeforeSeconds=before,AfterSeconds=after,Exact=nativeExact.IsChecked==true,Selection=NativeValue("scope")};
        batchDraft.HighlightFormat=format;
    }
    private void NativeEditChanged()
    {
        if(nativeLoading||NativeLocked)return;
        try { NativeReadDraft();BatchRecording.Save(DraftPath,batchDraft);nativeMessage.Foreground=Brushes.LightGray;nativeMessage.Text="Draft saved.";RefreshNativeDesk(); }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException) { nativeMessage.Text=error.Message; }
    }
    private void NativeApplyDraft()
    {
        bool wasLoading=nativeLoading;nativeLoading=true;
        try {
            nativeInputs["name"].Text=batchDraft.Name;nativeInputs["fov"].Text=batchDraft.Fov.ToString(CultureInfo.InvariantCulture);
            nativeInputs["seconds"].Text=(batchDraft.SampleSeconds??10).ToString(CultureInfo.InvariantCulture);
            NativeSet("mode",batchDraft.Mode);NativeSet("kind",batchDraft.CaptureKind);NativeSet("container",batchDraft.OutputFormat);
            NativeSet("resolution",batchDraft.OutputResolution.ToString(CultureInfo.InvariantCulture));NativeSet("quality",batchDraft.OutputQuality);
            var encoding=batchDraft.Encoding??new();nativeInputs["crf"].Text=encoding.Crf.ToString(CultureInfo.InvariantCulture);NativeSet("preset",encoding.Preset);NativeSet("tune",encoding.Tune);
            NativeSet("bitrate",encoding.CaptureBitrateMbps.ToString(CultureInfo.InvariantCulture));NativeSet("keyframe",encoding.KeyframeSeconds.ToString(CultureInfo.InvariantCulture));NativeSet("audio",encoding.AudioBitrateKbps.ToString(CultureInfo.InvariantCulture));
            nativeContinue.IsChecked=batchDraft.ContinueOnError;
            var options=batchDraft.HighlightOptions??new();nativeInputs["minimum"].Text=options.MinimumKills.ToString();nativeInputs["before"].Text=options.BeforeSeconds.ToString(CultureInfo.InvariantCulture);
            nativeInputs["after"].Text=options.AfterSeconds.ToString(CultureInfo.InvariantCulture);nativeExact.IsChecked=options.Exact;NativeSet("scope",options.Selection);
            var format=batchDraft.HighlightFormat??new();NativeSet("layout",format.Layout);nativeInputs["blur"].Text=format.Blur.ToString();nativeInputs["dim"].Text=format.Dim.ToString();nativeInputs["position"].Text=format.GameplayPosition.ToString();
        } finally { nativeLoading=wasLoading; }
    }

    private void RefreshNativeLibrary()
    {
        Catalog();NativeFilterLibrary();NativeUpdateArtwork();
    }
    private void NativeFilterLibrary()
    {
        var query=nativeSearch.Text.Trim();nativeLibrary.ItemsSource=recordings.Values.Where(f=>$"{f.Name} {f.TitleName} {f.Map} {f.GameVariantName}".Contains(query,StringComparison.OrdinalIgnoreCase)).OrderByDescending(f=>f.Modified).ToArray();
    }
    private void NativeUpdateArtwork()
    {
        nativeArtwork.Source=NativeImage((nativeLibrary.SelectedItem as Recording)?.ArtworkKey);
    }
    private BitmapImage? NativeImage(string? key)
    {
        if(key==null||!artwork.TryGetValue(key,out var url))return null;
        try {
            var root=Path.GetFullPath(Path.Combine(dataRoot,"MapArtwork"));var path=Path.GetFullPath(Path.Combine(root,Uri.UnescapeDataString(new Uri(url).AbsolutePath.TrimStart('/')).Replace('/',Path.DirectorySeparatorChar)));
            if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||!File.Exists(path))return null;
            var bitmap=new BitmapImage();bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();return bitmap;
        }catch(Exception e)when(e is IOException or ArgumentException or NotSupportedException){return null;}
    }
    private void NativePickFilms()
    {
        NativeRequireEditable();var dialog=new OpenFileDialog {Title="Add original Theater films",Filter="MCC Theater films (*.mov)|*.mov",Multiselect=true};
        if(Directory.Exists(preferences.Movies))dialog.InitialDirectory=preferences.Movies;
        if(dialog.ShowDialog(this)==true)NativeImport(dialog.FileNames);
    }
    private void NativeImport(IEnumerable<string> paths)
    {
        NativeRequireEditable();var films=new List<Recording>();var errors=new List<string>();
        foreach(var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))try{
            var film=ReadRecording(path);films.Add(film);if(!preferences.Recent.Contains(film.Path,StringComparer.OrdinalIgnoreCase))preferences.Recent.Add(film.Path);
        }catch(Exception e)when(e is IOException or UnauthorizedAccessException or ArgumentException){errors.Add($"{Path.GetFileName(path)}: {e.Message}");}
        SavePreferences();NativeFilterLibrary();NativeAddFilms(films);nativeTabs.SelectedIndex=1;
        if(errors.Count>0)nativeMessage.Text=string.Join("\n",errors.Take(4));
    }
    private void NativeAddFilms(IEnumerable<Recording> films)
    {
        NativeBeginEdit();int count=0;
        foreach(var film in films)if(!batchDraft.Items.Any(i=>i.Film.Equals(film.Path,StringComparison.OrdinalIgnoreCase))) {batchDraft.Items.Add(NativeBetaDraft.Item(film));count++;}
        BatchRecording.Save(DraftPath,batchDraft);RefreshNativeDesk();nativeMessage.Text=$"Added {count} film(s). Choose the matching MCC folder before export.";
    }
    private void NativeMoveSequence(int from,int to)
    {
        NativeBeginEdit();NativeBetaDraft.Move(batchDraft,from,to);BatchRecording.Save(DraftPath,batchDraft);RefreshNativeDesk();
        if(to>=0&&to<nativeSequence.Items.Count)nativeSequence.SelectedIndex=to;
    }
    private void NativeRemoveFilms()
    {
        var paths=nativeSequence.SelectedItems.Cast<BatchItem>().Select(i=>i.Film).ToHashSet(StringComparer.OrdinalIgnoreCase);NativeBeginEdit();
        batchDraft.Items.RemoveAll(i=>paths.Contains(i.Film));BatchRecording.Save(DraftPath,batchDraft);RefreshNativeDesk();
    }
    private void NativeNewReel()
    {
        NativeRequireEditable();
        if(batchDraft.Signature.Length>0)nativeRetryBatch=JsonSerializer.Deserialize<BatchPlan>(JsonSerializer.Serialize(batchDraft,Json),Json)!;
        batchDraft=new(){Fov=preferences.FovDegrees};NativeApplyDraft();BatchRecording.Save(DraftPath,batchDraft);RefreshNativeDesk();nativeMessage.Text="New reel ready.";
    }
    private void NativePickDestination()
    {
        NativeRequireEditable();var dialog=new OpenFolderDialog{Title="Choose export folder"};if(Directory.Exists(preferences.BatchOutputRoot))dialog.InitialDirectory=preferences.BatchOutputRoot;
        if(dialog.ShowDialog(this)==true){preferences.BatchOutputRoot=dialog.FolderName;SavePreferences();RefreshNativeDesk();}
    }

    private void BuildNativeHighlights()
    {
        var options=new StackPanel();var fields=new WrapPanel();options.Children.Add(fields);
        StackPanel Group(){var panel=new StackPanel{Width=155,Margin=new Thickness(0,0,10,0)};fields.Children.Add(panel);return panel;}
        NativeInput("minimum","Minimum kills (2–10)","4",Group());NativeChoice("scope","Find",new[]{("all","All matches"),("first","First in sequence"),("first-per-film","First per film")},Group());
        NativeInput("before","Seconds before", "3",Group());NativeInput("after","Seconds after","3",Group());
        options.Children.Add(nativeExact);nativeEditControls.Add(nativeExact);nativeExact.Click+=(_,_)=>NativeEditChanged();
        options.Children.Add(NativeBar(NativeAction("Find multikills",NativeScan,true),NativeAction("Cancel scan",()=>highlightScanCancellation?.Cancel()),
            NativeAction("Select all",()=>NativeSelectClips(true),true),NativeAction("Select none",()=>NativeSelectClips(false),true)));
        options.Children.Add(nativeScanMessage);
        nativeClips.IsReadOnly=false;nativeClips.Columns.Add(new DataGridCheckBoxColumn{Header="Export",Binding=new Binding("Selected"){Mode=BindingMode.TwoWay,UpdateSourceTrigger=UpdateSourceTrigger.PropertyChanged},Width=65});
        NativeColumn(nativeClips,"Multikill","Medal",1.2);NativeColumn(nativeClips,"Film","Film",2);NativeColumn(nativeClips,"Start tick","StartTick",.6);NativeColumn(nativeClips,"Seconds","DurationSeconds",.6);
        NativeColumn(nativeClips,"Caption above","Caption.Above",1.5,true);NativeColumn(nativeClips,"Caption below","Caption.Below",1.5,true);
        nativeClips.RowEditEnding+=(_,_)=>Dispatcher.BeginInvoke(new Action(NativeSaveCaptions));
        nativeClips.SelectionChanged+=(_,_)=>NativeDrawComposition();
        var footer=new StackPanel();var formatFields=new WrapPanel();footer.Children.Add(formatFields);
        StackPanel FormatGroup(){var panel=new StackPanel{Width=165,Margin=new Thickness(0,0,10,0)};formatFields.Children.Add(panel);return panel;}
        NativeChoice("layout","Clip format",new[]{("original","Landscape"),("soft-focus","Soft focus / portrait")},FormatGroup());
        NativeInput("blur","Blur (0–40)","22",FormatGroup());NativeInput("dim","Dim % (0–80)","40",FormatGroup());NativeInput("position","Vertical position % (30–65)","48",FormatGroup());
        footer.Children.Add(Text("Edit each clip’s captions in the table. Select a row to preview its portrait layout.",12));
        var preview=new Viewbox{Child=nativeComposition,Height=240,HorizontalAlignment=HorizontalAlignment.Left,Margin=new Thickness(0,8,0,8)};footer.Children.Add(preview);
        footer.Children.Add(Text("Layout uses map artwork. Render preview checks actual footage.",11));
        footer.Children.Add(NativeBar(NativeAction("Render preview",()=>NativeStartExport(true),true),NativeAction("Save highlight job…",()=>NativeSaveJob(true),true)));
        var content=NativePage(options,nativeClips,footer);
        nativeTabs.Items.Add(new TabItem {Header="Highlights",Content=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}});
        nativeClips.MinHeight=150;nativeClips.MaxHeight=360;
    }
    private void NativeSelectClips(bool selected) {NativeRequireEditable();if(highlightScan!=null)foreach(var clip in highlightScan.Clips)clip.Selected=selected;nativeClips.Items.Refresh();NativeSaveCaptions();}
    private void NativeSaveCaptions()
    {
        if(nativeLoading||NativeLocked||highlightScan==null)return;
        try {
            NativeBeginEdit();foreach(var clip in highlightScan.Clips){clip.Caption.Validate();batchDraft.HighlightCaptions[clip.Id]=clip.Caption;}
            BatchRecording.Save(DraftPath,batchDraft);File.WriteAllText(HighlightScanPath,JsonSerializer.Serialize(highlightScan,BatchRecording.Json));NativeDrawComposition();
        }catch(Exception e){NativeError(e);}
    }
    private async Task NativeScan()
    {
        NativeReadDraft();if(batchDraft.Items.Count==0)throw new IOException("Add films to the sequence first.");
        await HighlightMessage(JsonSerializer.SerializeToElement(new{type="highlights-scan",request=new HighlightScanRequest{Films=batchDraft.Items.Select(i=>i.Film).ToList(),Options=batchDraft.HighlightOptions!}},Json));
        if(highlightScan!=null)foreach(var clip in highlightScan.Clips)if(batchDraft.HighlightCaptions.TryGetValue(clip.Id,out var caption))clip.Caption=caption;
        RefreshNativeDesk();
    }
    private async Task NativeStartExport(bool preview)
    {
        NativeReadDraft();nativeClips.CommitEdit(DataGridEditingUnit.Cell,true);nativeClips.CommitEdit(DataGridEditingUnit.Row,true);NativeSaveCaptions();
        if(batchDraft.CaptureKind=="highlights"||preview){await HighlightMessage(NativeHighlightRequest("highlights-start",preview));}
        else await StartBatch(batchDraft);
        nativeTabs.SelectedIndex=3;RefreshNativeDesk();
    }
    private JsonElement NativeHighlightRequest(string type,bool preview=false)
    {
        if(!NativeBetaDraft.ScanMatches(highlightScan,batchDraft))throw new IOException("The sequence or scan options changed. Find multikills again before exporting.");
        var selected=highlightScan!.Clips.Where(c=>c.Selected).Select(c=>c.Id).ToArray();if(selected.Length==0)throw new IOException("Select at least one highlight clip.");
        return JsonSerializer.SerializeToElement(new{type,ids=selected,name=batchDraft.Name,mode=batchDraft.Mode,fov=batchDraft.Fov,continueOnError=batchDraft.ContinueOnError,
            outputFormat=batchDraft.OutputFormat,outputResolution=batchDraft.OutputResolution,outputQuality=batchDraft.OutputQuality,encoding=batchDraft.Encoding,format=batchDraft.HighlightFormat,captions=batchDraft.HighlightCaptions,preview},Json);
    }
    private void NativeSaveJob(bool highlights)
    {
        NativeReadDraft();NativeSaveCaptions();
        if(highlights||batchDraft.CaptureKind=="highlights") { _ = NativeSaveHighlights();return; }
        var plan=JsonSerializer.Deserialize<BatchPlan>(JsonSerializer.Serialize(batchDraft,Json),Json)!;PrepareBatch(plan);BatchRecording.Validate(plan);
        var dialog=new SaveFileDialog{Title="Save batch job",Filter="Batch job (*.json)|*.json",FileName=BatchRecording.SafeName(plan.Name)+".json"};
        if(dialog.ShowDialog(this)==true)BatchRecording.Save(dialog.FileName,plan);
    }
    private async Task NativeSaveHighlights(){try{await HighlightMessage(NativeHighlightRequest("highlights-save"));}catch(Exception e){NativeError(e);}}
    private void NativeLoadJob()
    {
        NativeRequireEditable();var dialog=new OpenFileDialog{Title="Load batch or highlight job",Filter="Capture job (*.json)|*.json"};if(dialog.ShowDialog(this)!=true)return;
        using var json=JsonDocument.Parse(File.ReadAllText(dialog.FileName));var schema=json.RootElement.GetProperty("schema").GetString();
        if(schema=="mcc-highlights/v1"){
            var loaded=HighlightRecording.Read(dialog.FileName);if(!Guid.TryParseExact(loaded.Id,"N",out _))throw new IOException("Invalid highlight job ID.");
            highlightRecording?.Dispose();highlightRecording=null;savedHighlightJob=loaded;HighlightRecording.Save(HighlightCurrentPath,loaded);nativeTabs.SelectedIndex=3;
        }else if(schema=="mcc-batch/v1"){
            var loaded=BatchRecording.Read(dialog.FileName);if(!Guid.TryParseExact(loaded.Id,"N",out _)||loaded.Items==null)throw new IOException("Invalid batch job.");
            nativeRetryBatch=loaded;batchRecording?.Dispose();batchRecording=null;batchDraft=loaded;NativeApplyDraft();BatchRecording.Save(DraftPath,batchDraft);RefreshNativeLibrary();
        }else throw new IOException("Choose a supported batch or highlight job.");
        RefreshNativeDesk();
    }
    private UIElement BuildNativeExports()
    {
        var page=new StackPanel{Margin=new Thickness(15)};page.Children.Add(Text("EXPORTS",20));page.Children.Add(nativeJobMessage);page.Children.Add(nativeJobProgress);
        page.Children.Add(NativeBar(
            NativeAction("Cancel export",async()=>{if(batchRecording?.IsRunning==true)await batchRecording.CancelAsync();if(highlightRecording?.IsRunning==true)await highlightRecording.CancelAsync();if(backgroundRecording?.IsRunning==true)await backgroundRecording.CancelAsync();RefreshNativeDesk();}),
            NativeAction("Retry batch",async()=>{NativeRequireEditable();await StartBatch(batchRecording?.Plan??nativeRetryBatch??(batchDraft.Signature.Length>0?batchDraft:throw new IOException("Load a batch checkpoint first.")));}),
            NativeAction("Retry highlights",async()=>{NativeRequireEditable();await StartHighlights(highlightRecording?.Plan??savedHighlightJob??throw new IOException("Load a highlight checkpoint first."));}),
            NativeAction("Open batch folder",()=>NativeOpenFolder(batchRecording?.Plan.OutputDirectory??nativeRetryBatch?.OutputDirectory)),
            NativeAction("Open highlight folder",()=>NativeOpenFolder((highlightRecording?.Plan??savedHighlightJob)?.OutputDirectory)),
            NativeAction("Open background folder",()=>backgroundRecording?.OpenFolder())));
        page.Children.Add(Text("Completed output files and errors appear below. Retry preserves the original checkpoint; editing the sequence creates a fresh draft.",12));
        nativeJobDetails=new TextBox{IsReadOnly=true,TextWrapping=TextWrapping.Wrap,MinHeight=220,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,12,0,0)};page.Children.Add(nativeJobDetails);
        return new ScrollViewer{Content=page,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
    }
    private TextBox nativeJobDetails=null!;
    private static void NativeOpenFolder(string? folder){if(folder==null||!Directory.Exists(folder))throw new IOException("No output folder exists yet.");Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={folder}});}
    private void RefreshNativeDesk()
    {
        if(nativeLoading||closing||nativeStart==null)return;
        var selected=(nativeSequence.SelectedItem as BatchItem)?.Film;nativeSequence.ItemsSource=null;nativeSequence.ItemsSource=batchDraft.Items;
        if(selected!=null)nativeSequence.SelectedItem=batchDraft.Items.FirstOrDefault(i=>i.Film==selected);
        bool scanMatches=NativeBetaDraft.ScanMatches(highlightScan,batchDraft);
        var clips=scanMatches?highlightScan?.Clips:null;
        if(!ReferenceEquals(nativeClips.ItemsSource,clips))nativeClips.ItemsSource=clips;
        nativeScanMessage.Text=highlightScanning?$"{highlightScanMessage} ({highlightScanProgress:P0})":scanMatches?$"{highlightScan?.Clips.Count??0} clips. {highlightScanMessage}":"Find multikills for the current sequence and options.";
        if(highlightScan!=null&&!highlightScanning&&scanMatches)nativeScanMessage.Text+="\n"+string.Join("\n",highlightScan.Films.Where(f=>f.Error.Length>0||f.Warnings.Count>0).Select(f=>$"{Path.GetFileName(f.Film)}: {f.Error} {string.Join("; ",f.Warnings)}"));
        foreach(var control in nativeEditControls)control.IsEnabled=!NativeLocked;
        nativeClips.IsReadOnly=NativeLocked;nativeInputs["seconds"].IsEnabled=!NativeLocked&&NativeValue("kind")=="sample";
        nativeStart.IsEnabled=!NativeLocked&&batchDraft.Items.Count>0;
        nativeDestination.Text=preferences.BatchOutputRoot;
        var batch=batchRecording?.Plan??nativeRetryBatch;var highlights=highlightRecording?.Plan??savedHighlightJob;
        var lines=new List<string>();double progressValue=0;
        if(batch!=null){lines.Add($"BATCH · {batch.State} · {batch.Progress:P0}\n{batch.Message}");progressValue=batch.Progress;}
        if(highlights!=null){lines.Add($"HIGHLIGHTS · {highlights.State} · {highlights.Progress:P0}\n{highlights.Message}\nCache: {highlights.CachedSources} reused, {highlights.CapturedSources} captured. {highlights.FootageCacheWarning}");progressValue=highlights.Progress;}
        if(backgroundStatus!=null)lines.Add($"BACKGROUND · {backgroundStatus.State}\n{backgroundStatus.Message}");
        nativeJobMessage.Text=lines.Count>0?string.Join("\n\n",lines):"No exports yet.";nativeJobProgress.Value=Math.Clamp(progressValue,0,1);
        nativeJobDetails.Text=string.Join("\n",(batch?.Items??new()).Select(i=>$"{i.Name} · {i.State}\n{i.File}\n{i.Error}"))+
            "\n"+string.Join("\n",(highlights?.Clips??new()).Select(c=>$"{c.Medal} · {c.State}\n{c.File}\n{c.Error}"))+
            (batch?.CombinedFile.Length>0?$"\nCombined: {batch.CombinedFile}":"")+(highlights?.CombinedFile.Length>0?$"\nCombined highlights: {highlights.CombinedFile}":"");
        NativeDrawComposition();
    }
    private void NativeDrawComposition()
    {
        nativeComposition.Children.Clear();var format=batchDraft.HighlightFormat??new();
        var clip=nativeClips.SelectedItem as HighlightClip;var film=clip!=null?recordings.Values.FirstOrDefault(f=>f.Path.Equals(clip.Film,StringComparison.OrdinalIgnoreCase)):null;
        var bitmap=NativeImage(film?.ArtworkKey);
        var backdrop=new Image{Source=bitmap,Width=216,Height=384,Stretch=Stretch.UniformToFill,Effect=new BlurEffect{Radius=format.Blur*.2}};nativeComposition.Children.Add(backdrop);
        nativeComposition.Children.Add(new System.Windows.Shapes.Rectangle{Width=216,Height=384,Fill=new SolidColorBrush(Color.FromArgb((byte)(255*format.Dim/100),0,0,0))});
        double top=format.GameplayTop*.2,height=HighlightFormat.GameplayHeight*.2;
        var gameplay=new Border{Width=216,Height=height,BorderBrush=Accent,BorderThickness=new Thickness(1),Background=Brushes.Black,
            Child=bitmap!=null?new Image{Source=bitmap,Stretch=Stretch.Uniform}:Text("FULL GAMEPLAY FRAME",11)};Canvas.SetTop(gameplay,top);nativeComposition.Children.Add(gameplay);
        void Caption(string text,double y,double h,double size){var label=new TextBlock{Text=text,Width=180,Height=Math.Max(0,h),TextWrapping=TextWrapping.Wrap,TextAlignment=TextAlignment.Center,Foreground=Brushes.White,FontSize=size,FontWeight=FontWeights.SemiBold};Canvas.SetLeft(label,18);Canvas.SetTop(label,y);nativeComposition.Children.Add(label);}
        Caption(clip?.Caption.Above??"",24,top-35,17.6);Caption(clip?.Caption.Below??"",top+height+12,338-top-height-12,10);
    }

    private Task ExerciseNativeDesk()
    {
        // Opt-in integration exercise runs only against the supplied test data directory.
        var original=batchDraft;var oldScan=highlightScan;
        try {
            void Check(bool condition,string name){if(!condition)throw new InvalidOperationException("Native UI exercise failed: "+name);Record("native-ui-check",new{name});}
            batchDraft=new(){HighlightOptions=new()};NativeApplyDraft();
            var a=new Recording("a","alpha.mov","test",100,"date",true,null,null,null,"00:10",600,Path.Combine(dataRoot,"alpha.mov"));
            var b=a with {Id="b",Name="beta.mov",Path=Path.Combine(dataRoot,"beta.mov")};
            NativeAddFilms(new[]{a,b,a});Check(batchDraft.Items.Count==2,"import deduplicates films");
            NativeMoveSequence(0,1);Check(batchDraft.Items[0].Name=="beta.mov","sequence reorder");
            nativeSequence.SelectedIndex=0;NativeRemoveFilms();Check(batchDraft.Items.Count==1&&batchDraft.Items[0].Name=="alpha.mov","sequence removal");
            batchDraft.Signature="frozen";batchDraft.SettingsSnapshot="frozen.ini";batchDraft.Items[0].File="keep.mp4";batchDraft.Items[0].State="completed";
            var frozen=batchDraft;nativeInputs["name"].Text="Edited reel";
            Check(batchDraft.Id!=frozen.Id&&batchDraft.Signature==""&&batchDraft.Items[0].File==""&&frozen.Items[0].File=="keep.mp4","editing forks immutable checkpoint");
            highlightScan=new(){Inputs=batchDraft.Items.Select(i=>i.Film).ToList(),Options=batchDraft.HighlightOptions!,Clips=new(){new(){Id="clip",Film=a.Path,Caption=new(){Above="Test caption"}}}};
            RefreshNativeDesk();Check(nativeClips.Items.Count==1,"matching scan visible");
            nativeInputs["minimum"].Text="5";Check(nativeClips.Items.Count==0,"changed scan options invalidate clips");
            nativeInputs["minimum"].Text="4";NativeSet("kind","highlights");NativeSaveCaptions();
            var request=NativeHighlightRequest("highlights-start");Check(request.GetProperty("ids").GetArrayLength()==1&&request.GetProperty("captions").GetProperty("clip").GetProperty("above").GetString()=="Test caption","highlight selection and captions");
            highlightScanning=true;RefreshNativeDesk();Check(!nativeInputs["name"].IsEnabled&&!nativeStart.IsEnabled&&nativeClips.IsReadOnly,"active scan locks editing");
            highlightScanning=false;RefreshNativeDesk();
            var saved=BatchRecording.Read(DraftPath);Check(saved.HighlightOptions?.MinimumKills==4&&saved.CaptureKind=="highlights","draft persists settings");
            nativeInputs["fov"].Text="121";bool rejected=false;try{NativeReadDraft();}catch(IOException){rejected=true;}Check(rejected,"invalid FOV rejected");
            Record("native-ui-exercise-complete",new{checks=10});
        } finally {highlightScanning=false;batchDraft=original;highlightScan=oldScan;nativeRetryBatch=null;NativeApplyDraft();BatchRecording.Save(DraftPath,batchDraft);RefreshNativeDesk();}
        return Task.CompletedTask;
    }

    private async Task ExerciseNativeExport()
    {
        // Explicit opt-in for a real sample through the same action as Start export.
        if(Argument("--data-dir")==null||batchDraft.CaptureKind!="sample"||batchDraft.SampleSeconds is not (>=1 and <=3))
            throw new IOException("The export exercise requires an isolated --data-dir and a 1–3 second sample draft.");
        await NativeStartExport(false);
        var owned=batchRecording??throw new IOException("Native export action did not start a batch.");
        await owned.Completion;
        if(owned.Plan.State!="completed") { Record("native-export-exercise-failed",new{owned.Plan.State,owned.Plan.Message});return; }
        Record("native-export-exercise-complete",new{files=owned.Plan.Items.Select(i=>i.File),owned.Plan.Progress});
    }
}
#endif
