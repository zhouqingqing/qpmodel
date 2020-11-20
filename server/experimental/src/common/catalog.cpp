#include "common/catalog.h"

#include "common/common.h"
#include "common/dbcommon.h"

namespace andb {

static SysTable* systable_ = new SysTable();
static SysStats* sysstat_ = new SysStats ();

// NOTE: None of the operations here are Multi-Thread (MT) safe.
bool SysTable::CreateTable (const std::string* tabName, std::vector<ColumnDef*>* columns,
                            std::string* distBy) {
    TableDef* tblDef = new TableDef (tabName, *columns);
    auto [it, added] = records_.insert (make_pair(tabName, tblDef));

    if (!added) {
        // ??? throw?
        return false;
    }

    return true;
}

void Catalog::createOptimizerTestTables () {
    const std::string colName{"i"};
    ColumnType colType{SQLType::SQL_INTEGER, TypeLenghts::lens_[(int)SQLType::SQL_INTEGER]};
    ColumnDef cd{&colName, colType, 0};

    std::vector<ColumnDef*> cols{&cd};

    for (int i = 0; i < 30; ++i) {
        std::string tname = "t";
        tname += std::to_string (i);
        Catalog::systable_->CreateTable (static_cast<const std::string*>(&tname), &cols);
    }
}

void Catalog::createBuiltInTestTables () {
    std::vector<std::string> tblNames{"a", "b", "c", "d"};
    std::vector<std::string> colNames{"a1", "a2", "a3", "a4", "b1", "b2", "b3", "b4",
                                      "c1", "c2", "c3", "c4", "d1", "d2", "d3", "d4"};
    ColumnType colType{SQLType::SQL_INTEGER, TypeLenghts::lens_[(int)SQLType::SQL_INTEGER]};
    for (int i = 0, j = 0; i < tblNames.size (); ++i) {
        ColumnDef cd1 (static_cast<const std::string*>(&colNames[j++]), colType, 0);
        ColumnDef cd2 (static_cast<const std::string*>(&colNames[j++]), colType, 0);
        ColumnDef cd3 (static_cast<const std::string*>(&colNames[j++]), colType, 0);
        ColumnDef cd4 (static_cast<const std::string*>(&colNames[j++]), colType, 0);
        std::vector<ColumnDef*> cols{&cd1, &cd2, &cd3, &cd4};
        Catalog::systable_->CreateTable (static_cast<const std::string*> (&tblNames[i]), &cols);
    }
}

}  // namespace andb
