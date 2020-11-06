#ifndef __CREATESTATEMENT_H_
#define __CREATESTATEMENT_H_
namespace andb {

#include <ostream>

#include "ColumnType.h"

// Note: Implementations of constructors and destructors can be found in statements.cpp.
  // Represents definition of a table column
  struct ColumnDefinition {
    ColumnDefinition(char* name, ColumnType type, bool nullable);
    virtual~ColumnDefinition();

    char* name;
    ColumnType type;
    bool nullable;
  };

  enum CreateType {
    kCreateTable,
    kCreateTableFromTbl, // Hyrise file format
    kCreateView
  };

  // Represents SQL Create statements.
  // Example: "CREATE TABLE students (name TEXT, student_number INTEGER, city TEXT, grade DOUBLE)"

  struct CreateStatement : SQLStatement {
    CreateStatement(CreateType type);
    ~CreateStatement() override;

    CreateType type;
    bool ifNotExists; // default: false
    char* filePath;   // default: nullptr
    char* schema;     // default: nullptr
    char* tableName;  // default: nullptr
    std::vector<ColumnDefinition*>* columns; // default: nullptr
    std::vector<char*>* viewColumns;
    SelectStatement* select;

    virtual LogicNode* CreatePlan (void) {
        return new LogicNode ();
    }

  };
}
#endif // __CREATESTATEMENT_H_
