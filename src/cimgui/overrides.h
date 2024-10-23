#pragma once

// Guard against people not reading the readme
#if !__has_include("imgui.h")
#error SETUP FAILURE: imgui.h not found. Your repo may not have submodules initialized correctly. Please run the following command: git submodule update --init --recursive
#endif
