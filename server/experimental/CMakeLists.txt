cmake_minimum_required(VERSION 3.8)
project(andb)

if(MSVC)
	set(CMAKE_CXX_FLAGS " /std:c++latest /EHsc")
	#
	# always static link c runtime libs
	# https://stackoverflow.com/questions/14172856/compile-with-mt-instead-of-md-using-cmake
	#
	set(CMAKE_CXX_FLAGS_RELEASE "${CMAKE_CXX_FLAGS_RELEASE} /MT")
	set(CMAKE_CXX_FLAGS_DEBUG "${CMAKE_CXX_FLAGS_DEBUG} /MTd")
else()
	set(CMAKE_CXX_FLAGS "-pthread -std=c++2a")
endif()

add_subdirectory(deps)
add_subdirectory(src)
add_subdirectory(bindings)
add_subdirectory(test)
