""" Usage: call with <filename> <typename>
"""

import sys
import clang.cindex

def find_typerefs(node, typename):
    """ Find all references to the type named 'typename'
    """
    if node.kind.is_reference():
        ref_node = node.get_definition()
        if ref_node:
            if ref_node.spelling == typename:
                print (typename, node.location.line, node.location.column)
    # Recurse for children of this node
    for c in node.get_children():
        find_typerefs(c, typename)

# use the llvm come with visual studio installation
clang.cindex.Config.set_library_path('C:/Program Files (x86)/Microsoft Visual Studio/2019/Community/VC/Tools/Llvm/x64/bin/')

index = clang.cindex.Index.create()
tu = index.parse(sys.argv[1])
print ('Translation unit:', tu.spelling)
find_typerefs(tu.cursor, sys.argv[2])