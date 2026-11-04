#ifndef NOMINMAX
#define NOMINMAX
#endif
#include "NativeFontAssets.h"
#include <windows.h>
#include <bcrypt.h>
#include <algorithm>
#include <array>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <map>
#include <mutex>
#include <set>
#include <string>
#pragma comment(lib,"bcrypt.lib")

namespace {
// FreeType's public Windows x64 ABI: long/FT_Pos/FT_Fixed are32bits; pointers
// are64bits. DLL SHA/version are verified before accessing public struct fields.
// Offsets match the existing audited build-navpoint-font.py reader.
static_assert(sizeof(long)==4&&sizeof(void*)==8,"FreeType Windows x64 ABI required");
struct Matrix {long xx,xy,yx,yy;};struct Vector {long x,y;};
struct FreeType {
 HMODULE module=nullptr;void* library=nullptr;
 int(*Init)(void**)=nullptr;int(*NewFace)(void*,const unsigned char*,long,long,void**)=nullptr;
 int(*SetSize)(void*,long,long,unsigned,unsigned)=nullptr;void(*SetTransform)(void*,const Matrix*,const Vector*)=nullptr;
 int(*LoadChar)(void*,unsigned long,int)=nullptr;unsigned(*CharIndex)(void*,unsigned long)=nullptr;
 int(*Kerning)(void*,unsigned,unsigned,unsigned,Vector*)=nullptr;void(*Version)(void*,int*,int*,int*)=nullptr;
 int(*SetProperty)(void*,const char*,const char*,const void*)=nullptr;
 int(*GetProperty)(void*,const char*,const char*,void*)=nullptr;
 int(*DoneFace)(void*)=nullptr;
} ft;
struct Asset {const char* name;uint64_t offset;uint32_t size;const char* hash;std::vector<unsigned char> bytes;};
std::array<Asset,13> assets={{
 {"Conduit",10319640576ull,70440,"59939819d1a9133171e8683a598965f84893a4f193e215a150b1821074855601",{}},
 {"Larabie",10346969088ull,44360,"52dd24f102fbffc8bb0ef87abf1e80c01b94bae4b872f6cfc2d808bc2c311d35",{}},
 {"Fixedsys",10337433600ull,575668,"6ee0f3573bc5e33e93b616ef6282f49bc0e227a31aa753ac76ed2e3f3d02056d",{}},
 {"TVNordCond",10369880064ull,71772,"61f142c2bee07912eee81f881f628031ba5d394da8471230b2f32da6a27f7b1b",{}},
 {"TVNordCondBold",10369953792ull,71496,"a54a0dddb6b28526cc165e08b19f437088921ff408612f953bc3c943678543bc",{}},
 {"Eurostile",10337384448ull,47652,"1930c267610090df328813570423a57f4fd17ef670370e8066377395831327b7",{}},
 {"Blender",10319570944ull,67288,"3ac514294181401e96acdfad1d0bb4012ba5469f42aafa2f423ac6c3ec2c72ae",{}},
 {"ConduitMedium",10338048000ull,52464,"c53f3202cc2b3787cf0fc081a26b3de76ca6d6d349b406e4decfc7898b9c66c0",{}},
 {"Pragmata",10365622272ull,69068,"ef8cec46cb475d3772c58b7361fefcc6a46909acefaae481c58bfde0d9184620",{}},
 // Original H4/H2A game fonts, read from the same installed PAK. ArameNumbers
 // supplies the gameplay score/ammo digits; Baksheesh supplies native HUD text.
 {"ArameNumbers",10295939072ull,35900,"82e54e1e1e3069209c791ec3de4964d9d621d7579947dec5036b2c513cd08ac1",{}},
 {"Baksheesh",10304768000ull,66936,"f9797aa36ff97647ab60b9455249ab73dd62a230a2f89f477c8c5f016b0e047a",{}},
 {"BaksheeshBold",10295977984ull,66448,"7c25282137b83ee65a2886449fe276422e5c5b635c3761e4faeeac3b0f880199",{}},
 {"BaksheeshThin",10317221888ull,66504,"004762c686e9fcfb2da61f04148ba1b2bdc4e00392b15d60de3b1b5f3c9331ba",{}}
}};
struct Face {void* value=nullptr;uint16_t ascent=0,descent=0;float scale=1;};
std::recursive_mutex mutex;std::map<std::string,Face> faces;std::set<std::string> reported;
void(*logMessage)(const char*,const char*)=nullptr;bool ready=false;
void Log(const std::string& value){if(logMessage)logMessage("native_font_asset",value.c_str());}
template<class T>T Field(void* source,size_t offset){T value{};std::memcpy(&value,static_cast<unsigned char*>(source)+offset,sizeof(value));return value;}
std::string Hash(const unsigned char* bytes,size_t size){
 BCRYPT_ALG_HANDLE algorithm=nullptr;BCRYPT_HASH_HANDLE hash=nullptr;std::array<unsigned char,32> digest{};
 NTSTATUS result=BCryptOpenAlgorithmProvider(&algorithm,BCRYPT_SHA256_ALGORITHM,nullptr,0);
 if(result>=0)result=BCryptCreateHash(algorithm,&hash,nullptr,0,nullptr,0,0);
 if(result>=0)result=BCryptHashData(hash,const_cast<unsigned char*>(bytes),static_cast<ULONG>(size),0);
 if(result>=0)result=BCryptFinishHash(hash,digest.data(),static_cast<ULONG>(digest.size()),0);
 if(hash)BCryptDestroyHash(hash);if(algorithm)BCryptCloseAlgorithmProvider(algorithm,0);if(result<0)return {};
 std::string text;constexpr char hex[]="0123456789abcdef";for(auto byte:digest){text+=hex[byte>>4];text+=hex[byte&15];}return text;
}
bool ReadFile(const std::filesystem::path& path,std::vector<unsigned char>& bytes){
 std::ifstream input(path,std::ios::binary|std::ios::ate);if(!input)return false;const auto length=input.tellg();
 if(length<=0||length>16*1024*1024)return false;bytes.resize(static_cast<size_t>(length));input.seekg(0);input.read(reinterpret_cast<char*>(bytes.data()),length);return !!input;
}
template<class T>bool Resolve(T& function,const char* name){
 function=reinterpret_cast<T>(GetProcAddress(ft.module,name));if(!function)Log(std::string("FreeType export missing: ")+name);return function!=nullptr;
}
Face* GetFace(const char* name,int pointSize,float scale){
 if(!ready||!name||pointSize<1||pointSize>256||!std::isfinite(scale)||scale<=0||scale>16)return nullptr;
 // Both public engine names refer to FF_BlenderProEditWide_L0.ufont.
 if(_stricmp(name,"BlenderProEditWide")==0)name="Blender";
 char key[160]{};std::snprintf(key,sizeof(key),"%s|%d|%.9g",name,pointSize,scale);
 auto existing=faces.find(key);if(existing!=faces.end())return &existing->second;
 auto asset=std::find_if(assets.begin(),assets.end(),[&](const Asset& value){return _stricmp(name,value.name)==0;});
 if(asset==assets.end()){if(reported.insert(name).second)Log(std::string("Unsupported MCC font request: ")+name+"; no Windows substitute");return nullptr;}
 Face face;face.scale=scale;
 if(ft.NewFace(ft.library,asset->bytes.data(),static_cast<long>(asset->bytes.size()),0,&face.value)||
    ft.SetSize(face.value,0,pointSize*64,96,96)){
   if(face.value)ft.DoneFace(face.value);Log(std::string("Cannot initialize installed MCC font: ")+key);return nullptr;
 }
 const long fixed=static_cast<long>(std::lround(scale*65536.0f));Matrix matrix{fixed,0,0,fixed};ft.SetTransform(face.value,&matrix,nullptr);
 void* size=Field<void*>(face.value,0x80);
 const int ascender=static_cast<int>(std::lround(Field<int32_t>(size,0x24)/64.0*scale));
 const int descender=static_cast<int>(std::lround(-Field<int32_t>(size,0x28)/64.0*scale));
 face.ascent=static_cast<uint16_t>(std::clamp(ascender,0,65535));face.descent=static_cast<uint16_t>(std::clamp(descender,0,65535));
 Log(std::string("Installed MCC ")+key+"; FreeType96DPI, ascent="+std::to_string(face.ascent)+", descent="+std::to_string(face.descent));
 return &faces.emplace(key,face).first->second;
}
}

bool InitializeNativeFontAssets(const wchar_t* mccRoot,void(*logger)(const char*,const char*)){
 std::lock_guard<std::recursive_mutex> lock(mutex);logMessage=logger;if(ready)return true;if(!mccRoot||!*mccRoot)return false;
 wchar_t executable[32768]{};const DWORD length=GetModuleFileNameW(nullptr,executable,static_cast<DWORD>(std::size(executable)));
 if(!length||length>=std::size(executable))return false;
 const auto dll=std::filesystem::path(executable).parent_path()/L"freetype.dll";
 std::vector<unsigned char> libraryBytes;
 if(!ReadFile(dll,libraryBytes)||Hash(libraryBytes.data(),libraryBytes.size())!="558f16aec5ae86588afa80d46d9f2029c3081dc21015017ef85db7cf25e293a6"){
   Log("Verified local freetype.dll is missing or has an unsupported identity");return false;}
 ft.module=LoadLibraryExW(dll.c_str(),nullptr,LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR|LOAD_LIBRARY_SEARCH_SYSTEM32);
 if(!ft.module){Log("Cannot load local FreeType runtime; Windows runtime dependencies may be missing");return false;}
 if(!Resolve(ft.Init,"FT_Init_FreeType")||!Resolve(ft.NewFace,"FT_New_Memory_Face")||!Resolve(ft.SetSize,"FT_Set_Char_Size")||
    !Resolve(ft.SetTransform,"FT_Set_Transform")||!Resolve(ft.LoadChar,"FT_Load_Char")||!Resolve(ft.CharIndex,"FT_Get_Char_Index")||
    !Resolve(ft.Kerning,"FT_Get_Kerning")||!Resolve(ft.Version,"FT_Library_Version")||!Resolve(ft.DoneFace,"FT_Done_Face")||
    !Resolve(ft.SetProperty,"FT_Property_Set")||!Resolve(ft.GetProperty,"FT_Property_Get")||ft.Init(&ft.library))return false;
 int major=0,minor=0,patch=0;ft.Version(ft.library,&major,&minor,&patch);
 if(major!=2||minor!=13||patch!=2){Log("Unexpected FreeType version");return false;}
 // The installed MCC host embeds FreeType 2.6.0. Its TrueType driver initializer
 // (EXE RVA250FDA0) selects the classic interpreter35, and its property setter
 // (2510810) accepts only that mode. The newer bundled DLL defaults to40;
 // select the game's mode before creating any faces or cached glyphs.
 const unsigned requestedInterpreter=35;unsigned effectiveInterpreter=0;
 if(ft.SetProperty(ft.library,"truetype","interpreter-version",&requestedInterpreter)||
    ft.GetProperty(ft.library,"truetype","interpreter-version",&effectiveInterpreter)||effectiveInterpreter!=requestedInterpreter){
   Log("Cannot select MCC's TrueType interpreter35");return false;
 }
 Log("TrueType interpreter35 selected and verified; matches installed MCC's FreeType2.6.0 driver mode");
 const auto pak=std::filesystem::path(mccRoot)/L"mcc/content/paks/MCC-WindowsNoEditor.pak";
 std::ifstream input(pak,std::ios::binary);if(!input){Log("Cannot read installed MCC font PAK");return false;}
 for(auto& asset:assets){asset.bytes.resize(asset.size);input.seekg(static_cast<std::streamoff>(asset.offset+53));
   input.read(reinterpret_cast<char*>(asset.bytes.data()),asset.size);
   if(!input||Hash(asset.bytes.data(),asset.bytes.size())!=asset.hash){Log(std::string("Installed font identity mismatch: ")+asset.name);return false;}
 }
 ready=true;Log("Verified thirteen installed MCC font assets; FreeType2.13.2; no Windows font substitutions");
 struct Expected {wchar_t code;int width,height,left,top,advance;};
 constexpr Expected checks[]={{L'R',28,45,6,45,36},{L'X',29,45,2,45,33},{L'Q',27,54,6,45,39}};
 uint16_t ascent=0,descent=0;
 bool valid=NativeFontAssetMetrics("Conduit",18,3.0f,&ascent,&descent)&&ascent==57&&descent==18;
 for(const auto& check:checks){NativeFontGlyph glyph;valid=NativeFontAssetGlyph("Conduit",18,3.0f,check.code,glyph)&&
    glyph.width==check.width&&glyph.height==check.height&&glyph.bearingX==check.left&&glyph.bearingY==check.top&&glyph.advance==check.advance&&valid;}
 valid=NativeFontAssetKerning("Conduit",18,3.0f,L'A',L'V')==-3&&valid;
 if(!valid){ready=false;Log("Installed MCC glyph self-check failed against audited18pt R/X/Q and AV metrics");return false;}
 Log("Audited18pt R/X/Q bitmap/bearing/advance,57/18 ascender/descender and AV=-3 kerning self-check passed");return true;
}
bool NativeFontAssetGlyph(const char* name,int pointSize,float scale,wchar_t code,NativeFontGlyph& glyph){
 std::lock_guard<std::recursive_mutex> lock(mutex);Face* face=GetFace(name,pointSize,scale);if(!face||!ft.CharIndex(face->value,code))return false;
 if(ft.LoadChar(face->value,code,4|8))return false; // FT_LOAD_RENDER | FT_LOAD_NO_BITMAP
 void* slot=Field<void*>(face->value,0x78);const auto width=Field<uint32_t>(slot,0x6C),height=Field<uint32_t>(slot,0x68);
 const auto pitch=Field<int32_t>(slot,0x70);const auto pixels=Field<unsigned char*>(slot,0x78);const auto mode=Field<uint8_t>(slot,0x82);
 if(width>1022||height>1022||(width&&height&&(!pixels||mode!=2)))return false;
 glyph={};glyph.width=static_cast<int>(width);glyph.height=static_cast<int>(height);
 glyph.bearingX=Field<int32_t>(slot,0x90);glyph.bearingY=Field<int32_t>(slot,0x94);glyph.advance=static_cast<int>(std::lround(Field<int32_t>(slot,0x58)/64.0));glyph.baseline=-static_cast<int>(face->descent);
 glyph.alpha.resize(static_cast<size_t>(width)*height);
 for(uint32_t row=0;row<height;++row){const auto* source=pitch>=0?pixels+static_cast<size_t>(row)*pitch:pixels+static_cast<size_t>(height-1-row)*static_cast<size_t>(-pitch);
   std::memcpy(glyph.alpha.data()+static_cast<size_t>(row)*width,source,width);}
 return true;
}
bool NativeFontAssetMetrics(const char* name,int pointSize,float scale,uint16_t* ascent,uint16_t* descent){
 std::lock_guard<std::recursive_mutex> lock(mutex);const Face* face=GetFace(name,pointSize,scale);if(!face)return false;
 if(ascent)*ascent=face->ascent;if(descent)*descent=face->descent;return true;
}
int NativeFontAssetKerning(const char* name,int pointSize,float scale,wchar_t left,wchar_t right){
 std::lock_guard<std::recursive_mutex> lock(mutex);Face* face=GetFace(name,pointSize,scale);if(!face||!left||!right)return 0;
 const auto a=ft.CharIndex(face->value,left),b=ft.CharIndex(face->value,right);Vector vector{};
 if(!a||!b||ft.Kerning(face->value,a,b,0,&vector))return 0;
 // FT_Get_Kerning is not transformed by FT_Set_Transform. The native engine
 // passes(current,next) and itself applies this result to both glyph and pen.
 return static_cast<int>(std::lround(vector.x/64.0*face->scale));
}
