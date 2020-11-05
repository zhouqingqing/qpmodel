#! /bin/sh

[ -f andb.l ] || {
   echo "Can't find andb.l"
   exit 1
}

[ -f andb.y ] || {
   echo "Can't find andb.l or andb.y"
    exit 1
}

for f in andb.l andb.y; do
    sed -e '
s/bison_parser.h/andb_parser.h/g
s/bison_parser.c/andb_parser.c/g
s/flex_lexer.h/andb_lexer.h/g
s/flex_lexer.cpp/andb_lexer.cpp/g
s/#.*include.*unistd.h.*/\
#ifndef _MSC_VER\
#include <unistd.h>\
#endif\
/
s/"flex_lexer.h"/"andb_lexer.h"/
s/"flex_lexer.cpp"/"andb_lexer.cpp"/
s:^[\t ]*//.*%output:%output:
s:^[\t ]*//.*%defines:%defines:
' $f > $f.new
done

mv andb.l.new andb_lexer.l || {
    echo "rename of x.new to x failed"
    exit 1
}
mv andb.y.new andb_parser.y || {
    echo "rename of x.new to x failed"
    exit 1
}

bison -l --verbose andb_parser.y || {
    echo "bison failed"
    exit 1
}

flex -L andb_lexer.l || {
    echo "flex failed"
    exit 1
}

for f in *.h *.cpp; do
   sed -i '
s/#.*include.*unistd\.h.*/\
#ifndef _MSC_VER\
#include <unistd.h>\
#endif\
/
/#.*line /d
' $f
done
