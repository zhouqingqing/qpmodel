project(andb)
file (GLOB_RECURSE SRC_LIST "${CMAKE_CURRENT_LIST_DIR}/*.cpp")

add_executable (andb ${SRC_LIST})

#depend libs
link_directories(../common ../parser ../optimizer ../runtime)
target_link_libraries(andb common parser optimizer runtime)
target_include_directories(andb PUBLIC ../../deps)