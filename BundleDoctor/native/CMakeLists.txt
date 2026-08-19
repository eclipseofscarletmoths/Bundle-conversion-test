cmake_minimum_required(VERSION 3.16)
project(BundleDoctorEtc2 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

if(NOT DEFINED ETC2COMP_SOURCE_DIR)
    message(FATAL_ERROR "ETC2COMP_SOURCE_DIR is required")
endif()

# Etc2Comp exposes the EtcLib target from its own EtcLib/CMakeLists.txt.
add_subdirectory("${ETC2COMP_SOURCE_DIR}/EtcLib" "etc2comp-lib")

add_library(bundle_doctor_etc2 SHARED native_etc2.cpp)
target_include_directories(bundle_doctor_etc2 PRIVATE
    "${ETC2COMP_SOURCE_DIR}/EtcLib/Etc"
    "${ETC2COMP_SOURCE_DIR}/EtcLib/EtcCodec"
)
target_link_libraries(bundle_doctor_etc2 PRIVATE EtcLib)
set_target_properties(bundle_doctor_etc2 PROPERTIES OUTPUT_NAME "bundle_doctor_etc2")
