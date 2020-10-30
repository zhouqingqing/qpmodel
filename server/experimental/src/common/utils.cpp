#include "common/memory.h"

thread_local DefaultResource defaultResource_;
thread_local MemoryResource* currentResource_ = &defaultResource_;

void test (void) {}