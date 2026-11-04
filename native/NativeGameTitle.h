#pragma once
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <string_view>

// Build identities are deliberately whole-file hashes, including signatures.
// Store and Steam are admitted separately; no unknown build is accepted merely
// because it exports the same factory or has a similar section layout.
struct NativeGameTitle {
    const char* folder;
    const wchar_t* wideFolder;
    const char* display;
    unsigned settingsIndex;
    uint32_t filmHeaderBytes;
    uint16_t filmMajor,filmMinor;
    const char* steamHash;
    const char* storeHash;
    bool legacyAudio;
    bool Halo3() const {return settingsIndex==2;}
    bool MatchesHash(std::string_view hash) const {return hash==steamHash||hash==storeHash;}
};
inline constexpr NativeGameTitle NativeGameTitles[]{
    {"halo3",L"halo3","Halo 3",2,0xFC80,10,1,
     "b209d8454b12dc77e54ccd2c9924ec8d44b8619d21cf98e36ffaf601e67efb63","fce2d92f06fccef3c4a66933ab280e90ec1ad705cfe71e68107816324d76ff6b",false},
    {"haloreach",L"haloreach","Halo: Reach",6,0x1F520,0x360E,2,
     "738dd2d24ea3aea12e1ee9aa4a61094bf116027d42004c35a19e5048608b0894","f9f39cf058ff28c298cc05964bc40898a57c30d33848fa54d950d8d6c2697e20",false},
    {"halo4",L"halo4","Halo 4",3,0x2D4E0,0x4C8F,1,
     "7c53e7d5bc9848545a1b70e2768242479336fba1b7630d7ab955f7fd0c34fa84","5767cd564c1e8e8d012d002a8de8e92960a3de46442399ed054e3c4ef44aa496",true},
    {"groundhog",L"groundhog","Halo 2 Anniversary",4,0x2D4E0,0x5530,1,
     "e51dda124799246a856af47d2888e057394424a0c9c8e92ae9099896164e6190","905b7ae23a2e12f99ba68f5c0b18ee50eee537cb90f002652f1757828bccff2c",true}
};
inline const NativeGameTitle* NativeTitleByName(std::wstring_view name) {
    for(const auto& title:NativeGameTitles)if(name==title.wideFolder)return &title;
    return nullptr;
}
inline const NativeGameTitle* NativeTitleByHash(std::string_view hash) {
    for(const auto& title:NativeGameTitles)if(title.MatchesHash(hash))return &title;
    return nullptr;
}
inline bool IsSupportedHalo3Hash(std::string_view hash){return NativeGameTitles[0].MatchesHash(hash);}
inline const NativeGameTitle* NativeFilmTitle(std::istream& input,std::string& error) {
    input.clear();input.seekg(0,std::ios::end);const auto end=input.tellg();input.seekg(0);
    if(end<12){error="Truncated Theater film";return nullptr;}
    const auto length=static_cast<uint64_t>(end);uint64_t offset=0;
    for(unsigned chunk=0;chunk<64&&offset<1024*1024&&offset+12<=length;++chunk){
        unsigned char h[12]{};input.seekg(static_cast<std::streamoff>(offset));input.read(reinterpret_cast<char*>(h),12);
        if(!input||(offset==0&&std::memcmp(h,"_blf",4))){error="Expected a native BLF Theater film";return nullptr;}
        const bool little=!std::memcmp(h,"flmd",4);
        const uint32_t size=little?uint32_t(h[4])|(uint32_t(h[5])<<8)|(uint32_t(h[6])<<16)|(uint32_t(h[7])<<24):
          (uint32_t(h[4])<<24)|(uint32_t(h[5])<<16)|(uint32_t(h[6])<<8)|h[7];
        if(size<12||size>length-offset){error="Invalid Theater film chunk bounds";return nullptr;}
        if(!std::memcmp(h,"flmh",4)){
            const uint16_t major=static_cast<uint16_t>((unsigned(h[8])<<8)|h[9]),minor=static_cast<uint16_t>((unsigned(h[10])<<8)|h[11]);
            for(const auto& title:NativeGameTitles)if(size==title.filmHeaderBytes&&major==title.filmMajor&&minor==title.filmMinor)return &title;
            error="Unsupported Theater film header identity";return nullptr;
        }
        if(!std::memcmp(h,"flmd",4)||!std::memcmp(h,"_eof",4))break;
        offset+=size;
    }
    error="Theater film header is missing";return nullptr;
}
inline const NativeGameTitle* NativeFilmTitle(const std::filesystem::path& file,std::string& error) {
    std::ifstream input(file,std::ios::binary);if(!input){error="Cannot read Theater film";return nullptr;}
    return NativeFilmTitle(input,error);
}
