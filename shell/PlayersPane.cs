using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Data;
using System.Windows.Media;

namespace MCCTheaterCapture;

internal sealed record PlayerSnapshot(int Index, string? Identifier, string? Xuid, string? Gamertag, int? Team,
    int? Kills, int? Deaths, int? Assists, int? Score, bool? Alive, bool? IsLocal, string? ServiceTag, string Source,
    int? PlayerDatum, int? UnitDatum, bool? Active, string? ModelName)
{
    public string Name => string.IsNullOrWhiteSpace(Gamertag) ? "Name unavailable" : Gamertag;
    public string KillsText => Kills?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string DeathsText => Deaths?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string AssistsText => Assists?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string ScoreText => Score?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public string TeamText => Team is >= 0 and <= 7 ? new[] {"Red", "Blue", "Green", "Orange", "Purple", "Gold", "Brown", "Pink"}[Team.Value] : Team?.ToString(CultureInfo.InvariantCulture) ?? "—";
    public bool CanWatch => Active!=false&&PlayerDatum is int player&&player!=-1&&UnitDatum is int unit&&unit!=-1;
}

internal sealed class PlayersPane : UserControl
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(186, 221, 130));
    private static readonly Brush Panel = new SolidColorBrush(Color.FromRgb(17, 25, 35));
    private readonly ObservableCollection<PlayerSnapshot> rows = new();
    private readonly DataGrid grid;
    private readonly TextBlock title, source, profile, selectionHint,selectedName;
    private readonly TabControl tabs;
    private readonly Button playerPov;
    private int? selectedIndex;
    private bool playerPovSupported,updatingRows;
    private int watchedPlayerDatum=-1;
    public event Action<int>? PlayerPovRequested;
    public IReadOnlyList<PlayerSnapshot> Players => rows.ToArray();
    public bool StatsAvailable { get; private set; }
    public string Source { get; private set; } = "film-header";
    public int? Tick { get; private set; }

    public PlayersPane()
    {
        var layout = new DockPanel { Background = Panel };
        var heading = new StackPanel { Margin = new Thickness(15,18,15,12) };
        title = Label("PLAYERS", 11); title.Foreground = Accent; title.FontWeight = FontWeights.SemiBold;
        source = Label("Reading recorded player identities…", 11); source.Margin = new Thickness(0,7,0,0);
        heading.Children.Add(title); heading.Children.Add(source); DockPanel.SetDock(heading,Dock.Top); layout.Children.Add(heading);
        tabs = new TabControl { Background=Panel, BorderThickness=new Thickness(0), Foreground=Brushes.LightGray, Margin=new Thickness(8,0,8,12) };
        var scoreboardTab = new TabItem { Header="Analysis scoreboard", Background=Panel, Foreground=Brushes.LightGray };
        var profileTab = new TabItem { Header="Recorded profile", Background=Panel, Foreground=Brushes.LightGray };
        var scoreboard = new DockPanel { Background=Panel, Margin=new Thickness(0,8,0,0) };
        selectionHint = Label("Select a player to inspect their recorded profile.", 10); selectionHint.Margin=new Thickness(0,9,0,0);
        grid = new DataGrid { ItemsSource=rows, AutoGenerateColumns=false, IsReadOnly=true, CanUserAddRows=false,
            CanUserDeleteRows=false, CanUserResizeRows=false, CanUserReorderColumns=false, CanUserSortColumns=false,
            SelectionMode=DataGridSelectionMode.Single, SelectionUnit=DataGridSelectionUnit.FullRow,
            HeadersVisibility=DataGridHeadersVisibility.Column, GridLinesVisibility=DataGridGridLinesVisibility.Horizontal,
            HorizontalGridLinesBrush=new SolidColorBrush(Color.FromRgb(41,57,73)), Background=Panel, Foreground=Brushes.LightGray,
            RowBackground=new SolidColorBrush(Color.FromRgb(23,33,43)), AlternatingRowBackground=Panel,
            BorderThickness=new Thickness(0), RowHeight=34, ColumnHeaderHeight=30, FontSize=11,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled, VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
        var headerStyle = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(Control.BackgroundProperty,new SolidColorBrush(Color.FromRgb(27,39,52))));
        headerStyle.Setters.Add(new Setter(Control.ForegroundProperty,Brushes.LightGray));
        headerStyle.Setters.Add(new Setter(Control.BorderThicknessProperty,new Thickness(0)));
        headerStyle.Setters.Add(new Setter(Control.PaddingProperty,new Thickness(5,6,3,6))); grid.ColumnHeaderStyle=headerStyle;
        grid.Columns.Add(Column("Player",nameof(PlayerSnapshot.Name),new DataGridLength(1,DataGridLengthUnitType.Star)));
        grid.Columns.Add(Column("K",nameof(PlayerSnapshot.KillsText),new DataGridLength(27)));
        grid.Columns.Add(Column("D",nameof(PlayerSnapshot.DeathsText),new DataGridLength(27)));
        grid.Columns.Add(Column("A",nameof(PlayerSnapshot.AssistsText),new DataGridLength(27)));
        grid.Columns.Add(Column("Score",nameof(PlayerSnapshot.ScoreText),new DataGridLength(42)));
        AutomationProperties.SetAutomationId(grid,"NativePlayerScoreboard");
        grid.SelectionChanged+=(_,_)=>{if(!updatingRows&&grid.SelectedItem is PlayerSnapshot player){selectedIndex=player.Index;UpdateProfile(player);}};
        grid.MouseDoubleClick+=(_,_)=>{if(grid.SelectedItem!=null)tabs.SelectedItem=profileTab;};
        scoreboard.Children.Add(grid);scoreboardTab.Content=scoreboard;
        var profileLayout = new StackPanel { Margin=new Thickness(12,18,12,12) };
        profile=Label("Select a player in the scoreboard.",12);profile.LineHeight=21;profileLayout.Children.Add(profile);
        playerPov=new Button { Content="Watch first-person (experimental)", IsEnabled=false, Background=new SolidColorBrush(Color.FromRgb(33,48,61)), Foreground=Brushes.White,
            Padding=new Thickness(10,10,10,10), Margin=new Thickness(0,8,0,0), BorderBrush=Accent,ToolTip="Alternate-player first-person is under investigation. Native player POV has not been proven." };
        AutomationProperties.SetAutomationId(playerPov,"WatchNativePlayerFirstPerson");
        AutomationProperties.SetName(playerPov,"Watch first-person (experimental)");
        playerPov.Click+=(_,_)=>{var player=rows.FirstOrDefault(p=>p.Index==selectedIndex);if(playerPovSupported&&player?.CanWatch==true)PlayerPovRequested?.Invoke(player.PlayerDatum!.Value);};
        var watchFooter=new StackPanel {Margin=new Thickness(15,8,15,15)};
        selectedName=Label("No player selected",12);selectedName.Foreground=Accent;
        watchFooter.Children.Add(selectedName);watchFooter.Children.Add(playerPov);watchFooter.Children.Add(selectionHint);
        DockPanel.SetDock(watchFooter,Dock.Bottom);layout.Children.Add(watchFooter);
        var note=Label("Custom analysis layout using native playback values. Recorded identities only; no online Xbox profile lookup.",10);note.Margin=new Thickness(0,20,0,0);profileLayout.Children.Add(note);
        profileTab.Content=new ScrollViewer {Content=profileLayout,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};tabs.Items.Add(scoreboardTab);tabs.Items.Add(profileTab);layout.Children.Add(tabs);Content=layout;
    }

    private static TextBlock Label(string value,double size)=>new(){Text=value,FontSize=size,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap};
    private static DataGridTextColumn Column(string header,string binding,DataGridLength width)
    {
        var style=new Style(typeof(TextBlock));style.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty,VerticalAlignment.Center));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty,new Thickness(5,0,3,0)));style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty,TextTrimming.CharacterEllipsis));
        return new DataGridTextColumn{Header=header,Binding=new Binding(binding),Width=width,ElementStyle=style};
    }

    public void LoadHeader(IReadOnlyList<RecordedPlayer> players)
    {
        Source="film-header";Tick=null;StatsAvailable=false;selectedIndex=null;
        Replace(players.Select(p=>new PlayerSnapshot(p.Index,p.Identifier,null,p.Gamertag,null,null,null,null,null,null,null,null,"film-header",null,null,null,null)));
    }

    public void ApplySnapshot(JsonElement value)
    {
        if(!value.TryGetProperty("players",out var players)||players.ValueKind!=JsonValueKind.Array)return;
        string snapshotSource=String(value,"source")??"native";bool available=Boolean(value,"statsAvailable")==true;
        var next=new List<PlayerSnapshot>();var indices=new HashSet<int>();
        foreach(var player in players.EnumerateArray())
        {
            int? index=Integer(player,"index")??Integer(player,"slot");if(index is null or <0 or >15||!indices.Add(index.Value))continue;
            next.Add(ReadPlayer(player,index.Value,snapshotSource,available));
        }
        // Startup can report an empty native array before the engine installs its
        // player globals. Retain the authored roster while those globals load.
        if(next.Count==0&&rows.Count>0&&!available)return;
        Source=snapshotSource;StatsAvailable=available;Tick=Integer(value,"tick");Replace(next);
    }

    public void ApplyProfile(JsonElement value)
    {
        var data=value.TryGetProperty("profile",out var nested)&&nested.ValueKind==JsonValueKind.Object?nested:value;
        int? index=Integer(data,"index")??Integer(data,"slot");if(index==null)return;
        var existing=rows.FirstOrDefault(p=>p.Index==index);if(existing==null)return;
        var updated=existing with { Gamertag=String(data,"gamertag")??String(data,"name")??existing.Gamertag,Identifier=String(data,"identifier")??existing.Identifier,
            Xuid=Identity(data,"xuid")??existing.Xuid,ServiceTag=String(data,"serviceTag")??existing.ServiceTag,Team=Integer(data,"team")??existing.Team,
            IsLocal=Boolean(data,"isLocal")??existing.IsLocal,Alive=Boolean(data,"alive")??existing.Alive,
            PlayerDatum=Integer(data,"playerDatum")??existing.PlayerDatum,UnitDatum=Integer(data,"unitDatum")??existing.UnitDatum,Active=Boolean(data,"active")??existing.Active,ModelName=AppearanceModel(data)??existing.ModelName };
        Replace(rows.Select(player=>player.Index==index?updated:player).ToArray());
    }

    public void SetCameraCapability(bool supported,int watchedDatum=-1,bool sceneReady=true)
    {
        playerPovSupported=supported&&sceneReady;watchedPlayerDatum=watchedDatum;
        var selected=rows.FirstOrDefault(p=>p.Index==selectedIndex);playerPov.IsEnabled=playerPovSupported&&selected?.CanWatch==true;
        playerPov.ToolTip=!supported?"Alternate-player first-person is under investigation. Native player POV has not been proven; recorded playback and 3D orbit remain available.":!sceneReady?"Wait for the native scene to finish loading before testing player POV.":"Laboratory experiment: use the selected live player's native camera. Alternate-player first-person has not been proven. Recorded camera returns to the original film viewpoint.";
    }

    private void Replace(IEnumerable<PlayerSnapshot> players)
    {
        var values=players.OrderBy(p=>p.Index).ToArray();
        var wantedIndex=selectedIndex;var wantedIdentifier=(grid.SelectedItem as PlayerSnapshot)?.Identifier;
        updatingRows=true;
        try
        {
            // Keep unchanged row objects and containers during frequent native
            // snapshots. Rebuilding the collection can steal an in-progress
            // mouse selection and send focus back to the first player.
            for(int position=0;position<values.Length;position++)
            {
                var next=values[position];
                if(position>=rows.Count){rows.Add(next);continue;}
                if(rows[position].Index!=next.Index)
                {
                    int existing=-1;for(int scan=position+1;scan<rows.Count;scan++)if(rows[scan].Index==next.Index){existing=scan;break;}
                    if(existing>=0)rows.Move(existing,position);else rows.Insert(position,next);
                }
                if(rows[position]!=next)rows[position]=next;
            }
            while(rows.Count>values.Length)rows.RemoveAt(rows.Count-1);
            var selected=(!string.IsNullOrEmpty(wantedIdentifier)?rows.FirstOrDefault(p=>string.Equals(p.Identifier,wantedIdentifier,StringComparison.OrdinalIgnoreCase)):null)
                ??rows.FirstOrDefault(p=>p.Index==wantedIndex)??rows.FirstOrDefault();
            if(selected!=null){if(!ReferenceEquals(grid.SelectedItem,selected))grid.SelectedItem=selected;selectedIndex=selected.Index;UpdateProfile(selected);}
            else{selectedIndex=null;profile.Text="No recorded player identities are available.";selectedName.Text="No player selected";playerPov.IsEnabled=false;}
        }
        finally{updatingRows=false;}
        title.Text=$"PLAYERS  {rows.Count}";
        source.Text=Source is "native" or "native-engine"?$"Native playback values · {(Tick is int tick?FormatTime(tick):"Live replay")}":"Recorded film roster · Live stats unavailable";
        selectionHint.Text=StatsAvailable?"Custom analysis layout; values come from native playback. Double-click a player for their recorded profile.":"Custom analysis layout. — means no verified statistic is available. Double-click for a recorded profile.";
    }

    private void UpdateProfile(PlayerSnapshot player)
    {
        string local=player.IsLocal==true?"Recorded local player":player.IsLocal==false?"Recorded remote player":"Local-player status unavailable";
        string alive=player.Alive==true?"Alive":player.Alive==false?"Not alive":"—";
        profile.Text=$"{player.Name}\n\nTeam  {player.TeamText}\nService tag  {player.ServiceTag??"—"}\nModel  {player.ModelName??"—"}\nArmor  —\nColors  —\nState  {alive}\n{local}\n\nKills  {player.KillsText}\nDeaths  {player.DeathsText}\nAssists  {player.AssistsText}\nScore  {player.ScoreText}\n\nRecorded player ID\n{player.Identifier??"—"}\n\nVerified XUID\n{player.Xuid??"—"}";
        playerPov.IsEnabled=playerPovSupported&&player.CanWatch;
        selectedName.Text="Selected: "+player.Name;
        if(player.PlayerDatum is int datum&&datum==watchedPlayerDatum)profile.Text+="\n\nWatching this player";
    }

    private static PlayerSnapshot ReadPlayer(JsonElement player,int index,string source,bool statsAvailable)=>new(index,String(player,"identifier"),Identity(player,"xuid"),String(player,"gamertag")??String(player,"name"),Integer(player,"team"),
        statsAvailable?Integer(player,"kills"):null,statsAvailable?Integer(player,"deaths"):null,statsAvailable?Integer(player,"assists"):null,statsAvailable?Integer(player,"score"):null,
        Boolean(player,"alive"),Boolean(player,"isLocal"),String(player,"serviceTag"),source,Integer(player,"playerDatum"),Integer(player,"unitDatum"),Boolean(player,"active"),AppearanceModel(player));
    private static string? AppearanceModel(JsonElement value)=>value.TryGetProperty("appearance",out var appearance)&&appearance.ValueKind==JsonValueKind.Object?String(appearance,"modelName"):null;
    private static string? String(JsonElement value,string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.String&&!string.IsNullOrWhiteSpace(item.GetString())?item.GetString():null;
    private static string? Identity(JsonElement value,string key)=>String(value,key)??(value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.Number?item.GetRawText():null);
    private static int? Integer(JsonElement value,string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind==JsonValueKind.Number&&item.TryGetInt32(out var result)?result:null;
    private static bool? Boolean(JsonElement value,string key)=>value.TryGetProperty(key,out var item)&&item.ValueKind is JsonValueKind.True or JsonValueKind.False?item.GetBoolean():null;
    private static string FormatTime(int tick)=>$"{Math.Max(0,tick)/3600:00}:{Math.Max(0,tick)/60%60:00}";
}
