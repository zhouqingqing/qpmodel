project(andb)

enable_testing()

# depend database libs
link_directories(../src/common ../src/parser ../src/optimizer ../src/runtime)
set(dblibs common parser optimizer runtime)

# functional test
#
set(t dbtest)
add_executable (${t} test.cpp)
link_directories(../src)
target_include_directories(${t} PRIVATE ${gtest_SOURCE_DIR}/include ${gtest_SOURCE_DIR})
target_link_libraries(${t} gtest ${dblibs})
target_include_directories(${t} PRIVATE ../deps)
# any errors will pop up a debug window
add_test(NAME ${t} COMMAND "${t} --gtest_break_on_failure")

# performance test
#
set(t dbperf)
add_executable (${t} "perf.cpp")
link_directories(../src)
target_link_libraries(${t} benchmark ${dblibs})
target_include_directories(${t} PRIVATE ../deps)
add_test(NAME ${t} COMMAND ${t})

