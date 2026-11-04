#pragma once
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#include <string>
bool InitializeTitleHud(HMODULE module,const char* title,void(*logger)(const char*,const char*));
bool TitleHudHiddenForCapture();
void SetTitlePlayerNameVisible(bool visible);
std::string TitleHudStatus();


