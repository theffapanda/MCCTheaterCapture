using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MCCTheaterCapture;

internal static class AudioCaptureCache
{
    private static string Hash(string path){using var input=File.OpenRead(path);return Convert.ToHexString(SHA256.HashData(input));}
    internal static string PathFor(string session,string film,string mccRoot,string? settings,int fov,double? seconds,int startTick=0,bool isolatedAudioPass=false)
    {
        settings??=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"AppData","LocalLow","MCC","Saved","Config","WindowsNoEditor","GameUserSettings.ini");
        var engines=new[]{"halo3","haloreach","halo4","groundhog"}.Select(title=>new{title,file=Path.Combine(mccRoot,title,title+".dll")})
            .Where(engine=>File.Exists(engine.file)).Select(engine=>new{engine.title,hash=Hash(engine.file)}).ToArray();
        var identity=JsonSerializer.Serialize(new{format="native-game-audio-48k-aac-v2-multititle",film=Path.GetFullPath(film),filmHash=Hash(film),mccRoot=Path.GetFullPath(mccRoot),engines,settingsHash=File.Exists(settings)?Hash(settings):"missing",fov,seconds});
        if(startTick>0)identity+="|ranged-v1|"+startTick.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if(isolatedAudioPass)identity+="|isolated-legacy-audio-qpc-v2";
        var key=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(session))!,"AudioCache",key+".mp4");
    }
    internal static bool Valid(string path)
    {
        try{return File.Exists(path)&&new FileInfo(path).Length>0&&File.Exists(path+".sha256")&&File.ReadAllText(path+".sha256")==Hash(path);}
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){return false;}
    }
    // Call only after the native audio pass has finalized and passed validation.
    internal static void Store(string source,string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{
            File.Copy(source,temporary);
            string digest=Hash(temporary);
            File.Move(temporary,destination,true);
            File.WriteAllText(temporary,digest);
            File.Move(temporary,destination+".sha256",true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
