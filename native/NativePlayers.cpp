#include "NativeGameTitle.h"
#include "NativePlayers.h"
#include <bcrypt.h>
#include <intrin.h>
#include <algorithm>
#include <array>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <initializer_list>
#include <mutex>
#include <set>
#include <sstream>
#include <string>
#include <vector>

namespace {
constexpr char kDllHash[] = "b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63";
constexpr size_t kTlsIndex = 0xA39F9C;
constexpr size_t kFilmTick = 0x1FD1E18;
constexpr size_t kPlayerStride = 0x2F8;
struct Player {
    int slot = -1;
    std::string identifier, name, serviceTag;
    size_t fileOffset = 0;
    int model = -1;
    bool native = false, stats = false;
    int32_t playerDatum = -1, unitDatum = -1, team = -1;
    std::array<int16_t, 11> channels{};
    bool scoreIsTotal = false;
};
std::mutex stateMutex;
std::vector<Player> initialPlayers, players;
std::vector<std::array<int, 2>> teamScores;
BYTE* image = nullptr;
void (*logger)(const char*, const char*) = nullptr;
bool nativeSupported = false, liveSeen = false, finalized = false;
bool teamsEnabled = false, teamsKnown = false;
int32_t acceptedTick = 0;
ULONGLONG lastSample = 0;
std::string error;

void Log(const char* event, const char* detail) { if (logger) logger(event, detail); }
template<class T> bool Read(const void* pointer, T& value) {
    SIZE_T read = 0;
    return pointer && ReadProcessMemory(GetCurrentProcess(), pointer, &value, sizeof(value), &read) && read == sizeof(value);
}
bool ReadBytes(const void* pointer, void* output, size_t bytes) {
    SIZE_T read = 0;
    return pointer && ReadProcessMemory(GetCurrentProcess(), pointer, output, bytes, &read) && read == bytes;
}
template<class T> T Value(const BYTE* bytes, size_t offset) { T value{}; std::memcpy(&value, bytes + offset, sizeof(value)); return value; }
uint32_t Big32(const BYTE* p) { return (uint32_t(p[0])<<24)|(uint32_t(p[1])<<16)|(uint32_t(p[2])<<8)|p[3]; }
uint16_t Big16(const BYTE* p) { return uint16_t((uint16_t(p[0])<<8)|p[1]); }
std::string Hex(const BYTE* bytes, size_t count) {
    std::string result; result.reserve(count*2);
    constexpr char digits[] = "0123456789abcdef";
    for (size_t i=0;i<count;++i) { result += digits[bytes[i]>>4]; result += digits[bytes[i]&15]; }
    return result;
}
std::string Text(const BYTE* bytes, size_t characters) {
    std::wstring text;
    for (size_t i=0;i<characters;++i) {
        const wchar_t c = static_cast<wchar_t>(Value<uint16_t>(bytes,i*2));
        if (!c) break;
        if (c<32 || c==127) return {};
        text.push_back(c);
    }
    if (text.empty()) return {};
    const int count=WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),nullptr,0,nullptr,nullptr);
    if (count<=0) return {};
    std::string result(static_cast<size_t>(count),'\0');
    WideCharToMultiByte(CP_UTF8,WC_ERR_INVALID_CHARS,text.data(),static_cast<int>(text.size()),result.data(),count,nullptr,nullptr);
    return result;
}
std::string Quote(const std::string& value) {
    std::string result="\"";
    constexpr char hex[]="0123456789abcdef";
    for (const unsigned char c:value) {
        if (c=='"' || c=='\\') { result+='\\'; result+=static_cast<char>(c); }
        else if (c<32) { result+="\\u00";result+=hex[c>>4];result+=hex[c&15]; }
        else result+=static_cast<char>(c);
    }
    return result+'"';
}
std::string FileHash(HMODULE module) {
    wchar_t filename[32768]{};
    if (!GetModuleFileNameW(module,filename,static_cast<DWORD>(std::size(filename)))) return {};
    std::ifstream input(std::filesystem::path(filename),std::ios::binary);
    if (!input) return {};
    BCRYPT_ALG_HANDLE algorithm{}; BCRYPT_HASH_HANDLE hash{}; DWORD length=0,read=0;
    if (BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0)<0) return {};
    if (BCryptGetProperty(algorithm,BCRYPT_OBJECT_LENGTH,reinterpret_cast<PUCHAR>(&length),sizeof(length),&read,0)<0) { BCryptCloseAlgorithmProvider(algorithm,0); return {}; }
    std::vector<UCHAR> object(length);
    if (BCryptCreateHash(algorithm,&hash,object.data(),length,nullptr,0,0)<0) { BCryptCloseAlgorithmProvider(algorithm,0); return {}; }
    std::array<char,65536> buffer{}; bool ok=true;
    while (input) { input.read(buffer.data(),buffer.size()); if(input.gcount() && BCryptHashData(hash,reinterpret_cast<PUCHAR>(buffer.data()),static_cast<ULONG>(input.gcount()),0)<0){ok=false;break;} }
    BYTE digest[32]{}; ok=ok&&input.eof()&&BCryptFinishHash(hash,digest,sizeof(digest),0)>=0;
    BCryptDestroyHash(hash); BCryptCloseAlgorithmProvider(algorithm,0);
    return ok?Hex(digest,sizeof(digest)):std::string{};
}
bool ReadRoster(const wchar_t* path) {
    if (!path) { error="Original film path is missing";return false; }
    std::ifstream input(std::filesystem::path(path),std::ios::binary);
    if (!input) { error="Unable to read original film header";return false; }
    input.seekg(0,std::ios::end); const auto end=input.tellg();
    if (end<12) {error="Original film is truncated";return false;}
    const uint64_t length=static_cast<uint64_t>(end);
    uint64_t offset=0;
    for(int chunks=0;chunks<64&&offset<=1024*1024;++chunks) {
        BYTE header[12]{};
        if(offset>length-sizeof(header)){error="Invalid film chunk extent";return false;}
        input.seekg(static_cast<std::streamoff>(offset)); input.read(reinterpret_cast<char*>(header),sizeof(header));
        if(!input || (offset==0&&std::memcmp(header,"_blf",4))){error="Initial roster requires an original BLF film";return false;}
        const uint32_t size=!std::memcmp(header,"flmd",4)?Value<uint32_t>(header,4):Big32(header+4);
        if(size<sizeof(header)||size>length-offset){error="Invalid film chunk extent";return false;}
        if(!std::memcmp(header,"flmh",4)) {
            if(size!=0xFC80||Big16(header+8)!=10||Big16(header+10)!=1){error="Unaudited initial film roster layout";return false;}
            std::vector<BYTE> filmHeader(size);input.seekg(static_cast<std::streamoff>(offset));input.read(reinterpret_cast<char*>(filmHeader.data()),size);
            if(!input){error="Truncated native film header";return false;}
            // Existing film-game-variant reader and native scoreboard trace:
            // game variant starts at flmh+1718, teams flag at variant+104 bit 0.
            const auto engine=Value<uint32_t>(filmHeader.data(),0x1718);
            teamsKnown=engine<=12;teamsEnabled=teamsKnown&&(filmHeader[0x1718+0x104]&1)!=0;
            std::set<std::string> identities;
            for(int slot=0;slot<16;++slot) {
                const size_t entry=0x310+static_cast<size_t>(slot)*0xB8;
                if(!filmHeader[entry])continue;
                Player p; p.slot=slot;p.fileOffset=static_cast<size_t>(offset)+entry;
                p.identifier=Hex(filmHeader.data()+entry+0x10,8);
                if(p.identifier=="0000000000000000"||!identities.insert(p.identifier).second){error="Invalid initial roster identifier";return false;}
                p.name=Text(filmHeader.data()+entry+0x18,16);
                p.serviceTag=Text(filmHeader.data()+entry+0x56,4);
                // Compared against all eight original film configuration updates.
                // Color and armor encodings differ from the packed stream, so
                // this first POC intentionally exposes only verified model/tag.
                const int model=filmHeader[entry+0x3C];p.model=model<=1?model:-1;
                initialPlayers.push_back(p);
            }
            if(initialPlayers.empty()){error="Original film header contains no initial players";return false;}
            return true;
        }
        if(!std::memcmp(header,"_eof",4))break;
        offset+=size;
    }
    error="Native film header was not found";return false;
}
BYTE* EngineTls() {
    DWORD index=0;if(!Read(image+kTlsIndex,index)||index>=1088)return nullptr;
    auto table=reinterpret_cast<BYTE**>(__readgsqword(0x58)); BYTE* tls=nullptr;
    return Read(table+index,tls)?tls:nullptr;
}
bool Signature(size_t rva,std::initializer_list<BYTE> expected) {
    std::vector<BYTE> actual(expected.size());
    return ReadBytes(image+rva,actual.data(),actual.size())&&std::equal(actual.begin(),actual.end(),expected.begin());
}
}

bool InitializeNativePlayers(HMODULE module,const wchar_t* originalFilmPath,void(*log)(const char*,const char*)) {
    std::lock_guard<std::mutex> lock(stateMutex);
    logger=log;image=reinterpret_cast<BYTE*>(module);initialPlayers.clear();players.clear();teamScores.clear();
    error.clear();acceptedTick=0;liveSeen=false;finalized=false;lastSample=0;nativeSupported=false;teamsEnabled=false;teamsKnown=false;
    const bool roster=ReadRoster(originalFilmPath);if(!roster)initialPlayers.clear();players=initialPlayers;
    if(roster){const auto detail=std::to_string(players.size())+" original film-header profiles; recorded identifiers are not asserted to be Xbox account IDs";Log("native_players_roster_ready",detail.c_str());}
    else Log("native_players_roster_unavailable",error.c_str());
    nativeSupported=module&&IsSupportedHalo3Hash(FileHash(module))&&
        Signature(0x10057C,{0x48,0x83,0xEC,0x28,0x45,0x33,0xC0,0xE8})&&
        Signature(0x1DA600,{0x48,0x89,0x5C,0x24,0x08,0x48,0x89,0x54,0x24,0x10});
    Log(nativeSupported?"native_players_adapter_ready":"native_players_adapter_unavailable",nativeSupported?"Version-pinned own-process player data and native statborg; awaiting engine TLS":"Native DLL hash/signature does not match verified player/statborg layout");
    return roster;
}

void NativePlayersTick() {
    std::lock_guard<std::mutex> lock(stateMutex);
    if(!nativeSupported)return;
    const auto now=GetTickCount64();if(lastSample&&now-lastSample<100)return;lastSample=now;
    BYTE* tls=EngineTls(); BYTE* dataArray=nullptr;BYTE* records=nullptr;BYTE* engineGlobals=nullptr;BYTE* allocationBits=nullptr;
    int32_t tick=0,stride=0,count=0;uint8_t arrayValid=0;uint32_t allocated=0;
    if(!tls||!Read(image+kFilmTick,tick)||tick<=0||
        !Read(tls+0x10,dataArray)||!dataArray||!Read(dataArray+0x24,stride)||stride!=kPlayerStride||
        !Read(dataArray+0x29,arrayValid)||!arrayValid||
        !Read(dataArray+0x3C,count)||count<1||count>16||!Read(dataArray+0x48,records)||!records||
        !Read(dataArray+0x50,allocationBits)||!allocationBits||!Read(allocationBits,allocated)||
        !Read(tls+0x20,engineGlobals)||!engineGlobals)return;
    std::vector<BYTE> nativeRecords(static_cast<size_t>(count)*kPlayerStride);
    std::array<BYTE,0x4C9> statborg{};
    if(!ReadBytes(records,nativeRecords.data(),nativeRecords.size())||!ReadBytes(engineGlobals+0xFEB8,statborg.data(),statborg.size()))return;
    std::vector<Player> next;next.reserve(static_cast<size_t>(count));
    for(int slot=0;slot<count;++slot) {
        // Native iterator 13091C checks data-array allocation bits at +0x50.
        if(!(allocated&(1u<<slot)))continue;
        const auto record=nativeRecords.data()+static_cast<size_t>(slot)*kPlayerStride;
        const auto salt=Value<uint16_t>(record,0);if(!salt)continue;
        // Native current-player iterator E72B0 skips flags bit 1 (left game).
        if(Value<uint32_t>(record,4)&2u)continue;
        // E2A18..E2A1C copies options+0x10 into player+8. Initial film header
        // identities are optional provenance: first playback updates can replace
        // lobby members and introduce players absent from the header entirely.
        const auto identifier=Hex(record+0x8,8);
        if(identifier=="0000000000000000")continue;
        const auto initial=std::find_if(initialPlayers.begin(),initialPlayers.end(),[&](const Player& p){return p.identifier==identifier;});
        Player player=initial!=initialPlayers.end()?*initial:Player{};
        if(player.slot<0)player.slot=slot;
        player.identifier=identifier;auto row=&player;
        row->native=true;row->stats=true;
        row->playerDatum=static_cast<int32_t>((uint32_t(salt)<<16)|static_cast<uint32_t>(slot));
        row->unitDatum=Value<int32_t>(record,0x28);
        // E2370/E25DD copy the accepted 0xA0-byte configuration to player+58:
        // name +0, appearance model +24, service tag +3E within that config.
        const auto name=Text(record+0x58,16);if(!name.empty())row->name=name;
        row->serviceTag=Text(record+0x96,4);
        const int model=record[0x7C];row->model=model<=1?model:-1;
        const int team=Value<int32_t>(record,0xF0);row->team=team>=0&&team<=7?team:-1;
        const auto stats=statborg.data()+static_cast<size_t>(slot)*0x40;
        for(size_t channel=0;channel<row->channels.size();++channel)row->channels[channel]=Value<int16_t>(stats,channel*2);
        row->scoreIsTotal=stats[0x2E]!=0;
        next.push_back(std::move(player));
    }
    if(next.empty())return;
    players=std::move(next);acceptedTick=tick;finalized=statborg[0x4C8]!=0;teamScores.clear();
    for(int team=0;team<8;++team) {
        if(!teamsEnabled)break;
        if(std::none_of(players.begin(),players.end(),[&](const Player& p){return p.native&&p.team==team;}))continue;
        const auto stats=statborg.data()+0x400+static_cast<size_t>(team)*0x18;
        const int round=Value<int16_t>(stats,0),total=Value<int16_t>(stats,2);
        teamScores.push_back({team,total+(stats[0x16]?0:round)});
    }
    if(!liveSeen){liveSeen=true;Log("native_players_live_ready","Allocated current native players, accepted configurations and native statborg channels; header roster is optional provenance");}
}

std::string SnapshotNativePlayersJson() {
    std::lock_guard<std::mutex> lock(stateMutex);
    std::ostringstream json;
    json<<"{\"type\":\"players\",\"schema\":\"mcc-native-players/v1\",\"source\":"<<Quote(liveSeen?"native-engine":"original-film-header")
        <<",\"tick\":"<<acceptedTick<<",\"statsAvailable\":"<<(liveSeen?"true":"false")<<",\"finalized\":"<<(finalized?"true":"false")
        <<",\"teamsEnabled\":"<<(teamsKnown?(teamsEnabled?"true":"false"):"null")
        <<",\"identitySource\":\"recorded\",\"error\":"<<(error.empty()?"null":Quote(error))<<",\"players\":[";
    bool first=true;
    for(const auto& p:players) {
        if(!first)json<<',';first=false;
        json<<"{\"index\":"<<(p.native?(uint32_t(p.playerDatum)&0xFFFFu):static_cast<uint32_t>(p.slot))<<",\"slot\":"<<p.slot<<",\"identifier\":"<<Quote(p.identifier)<<",\"identitySource\":\"recorded\",\"xuid\":null,\"name\":"<<Quote(p.name)<<",\"gamertag\":"<<Quote(p.name)
            <<",\"serviceTag\":"<<Quote(p.serviceTag)<<",\"profileSource\":"<<Quote(p.native?"native-player-configuration":"original-film-header")<<",\"fileOffset\":"<<(p.fileOffset?std::to_string(p.fileOffset):"null")<<",\"appearance\":{";
        json<<"\"model\":"<<(p.model>=0?std::to_string(p.model):"null")<<",\"modelName\":"<<(p.model==0?Quote("Spartan"):p.model==1?Quote("Elite"):"null")<<",\"armor\":null,\"colors\":null},\"team\":"<<(teamsEnabled&&p.team>=0?std::to_string(p.team):"null")
            <<",\"playerDatum\":"<<(p.native?std::to_string(p.playerDatum):"null")<<",\"unitDatum\":"<<(p.native&&p.unitDatum!=-1?std::to_string(p.unitDatum):"null")<<",\"active\":"<<(p.native?"true":"null")<<",\"leftGame\":"<<(p.native?"false":"null");
        if(p.stats)json<<",\"statsSource\":\"native-statborg\",\"kills\":"<<p.channels[2]<<",\"deaths\":"<<p.channels[3]<<",\"assists\":"<<p.channels[6]<<",\"score\":"<<(p.channels[1]+(p.scoreIsTotal?0:p.channels[0]))<<",\"roundScore\":"<<p.channels[0]<<",\"totalScore\":"<<p.channels[1]<<",\"roundsWon\":"<<p.channels[7];
        else json<<",\"statsSource\":null,\"kills\":null,\"deaths\":null,\"assists\":null,\"score\":null,\"roundScore\":null,\"totalScore\":null,\"roundsWon\":null";
        json<<'}';
    }
    json<<"],\"teams\":[";first=true;
    for(const auto& team:teamScores){if(!first)json<<',';first=false;json<<"{\"team\":"<<team[0]<<",\"score\":"<<team[1]<<'}';}
    json<<"]}";return json.str();
}
