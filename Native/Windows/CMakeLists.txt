cmake_minimum_required(VERSION 3.16)
project(VoiceCapture CXX)

set(CMAKE_CXX_STANDARD 17)

# Windows WASAPI DLL
add_library(VoiceCapture SHARED VoiceCapture.cpp)
target_link_libraries(VoiceCapture PRIVATE avrt ole32 uuid)
set_target_properties(VoiceCapture PROPERTIES
    WINDOWS_EXPORT_ALL_SYMBOLS OFF
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../../bin/Windows"
)

# Compiler flags for low latency (no unnecessary checks in release)
target_compile_options(VoiceCapture PRIVATE
    $<$<CONFIG:Release>:/O2 /Ot /GL>
    $<$<CONFIG:Debug>:/Od /Zi>
)
