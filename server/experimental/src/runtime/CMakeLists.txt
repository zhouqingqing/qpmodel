project(andb)
file (GLOB_RECURSE SRC_LIST "${CMAKE_CURRENT_LIST_DIR}/*.cpp")

set(t runtime)
add_library(${t} STATIC ${SRC_LIST})
target_include_directories(${t} PUBLIC ../include)
target_include_directories(${t} PUBLIC ..)
target_include_directories(${t} PUBLIC ../../deps)