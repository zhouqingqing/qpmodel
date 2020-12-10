#include <string>
#include <utility>
#include <map>

#include "common/common.h"
#include "common/dbcommon.h"
#include "common/catalog.h"

// NOTE: None of the operations here are Multi-Thread (MT) safe.

namespace andb {

NEW_SYSTABLE_TYPE();
NEW_SYSSTATS_TYPE();

static std::vector<std::string> builtinTestTableNames{ "a", "b", "c", "d" };

TableDef* SysTable::TryTable(std::string* tblName)
{
    auto it = records_.find(tblName);
    if (it != records_.end())
        return it->second;

    return nullptr;
}

ColumnDef* SysTable::Column(std::string* colName, std::string* tblName)
{
    TableDef* td = TryTable(colName);
    if (td != nullptr) {
        auto it = td->columns_->find(colName);
        if (it != td->columns_->end())
            return it->second;
    }

    return nullptr;
}

bool SysTable::CreateTable(std::string*             tabName,
                           std::vector<ColumnDef*>* columns,
                           std::string*             distBy)
{
    TableDef*                          tblDef = new TableDef(tabName, *columns);
    std::pair<std::string*, TableDef*> mapVal{ tblDef->name_, tblDef };
    auto [it, added] = records_.insert(mapVal);

    if (!added) {
        // ??? throw?
        return false;
    }

    return true;
}

// Let tables and their columns do their own cleanup.
void SysTable::dropAllTables()
{
#ifdef __RUN_DELETES_
    for (auto t : records_) {
        delete t.second;
    }

    records_.clear();
#endif // __RUN_DELETES_
}

void Catalog::createOptimizerTestTables()
{
    std::string colName{ "i" };
    ColumnType   colType{ SQLType::SQL_TYPE_INTEGER,
                        TypeLenghts::lens_[(int)SQLType::SQL_TYPE_INTEGER] };

    for (int i = 0; i < 30; ++i) {
        std::string             tname{ "t" };
        ColumnDef    cd{&colName, colType, 0 };
        std::vector<ColumnDef*> cols{ &cd };
        tname += std::to_string(i);
        Catalog::systable_->CreateTable(static_cast<std::string*>(&tname), &cols);
        cd.name_ = nullptr;
    }
}

void Catalog::createBuiltInTestTables()
{

    std::vector<std::string> colNames{ "a1", "a2", "a3", "a4", "b1", "b2", "b3", "b4",
                                       "c1", "c2", "c3", "c4", "d1", "d2", "d3", "d4" };
    ColumnType               colType{ SQLType::SQL_TYPE_INTEGER,
                        TypeLenghts::lens_[(int)SQLType::SQL_TYPE_INTEGER] };
    for (int i = 0, j = 0; i < builtinTestTableNames.size(); ++i) {
        ColumnDef               cd1(static_cast<std::string*>(&colNames[j++]), colType, 0);
        ColumnDef               cd2(static_cast<std::string*>(&colNames[j++]), colType, 1);
        ColumnDef               cd3(static_cast<std::string*>(&colNames[j++]), colType, 2);
        ColumnDef               cd4(static_cast<std::string*>(&colNames[j++]), colType, 3);
        std::vector<ColumnDef*> cols{ &cd1, &cd2, &cd3, &cd4 };
        Catalog::systable_->CreateTable(static_cast<std::string*>(&builtinTestTableNames[i]),
                                        &cols);
        cd1.name_ = cd2.name_ = cd3.name_ = cd4.name_ = nullptr;
    }
}
} // namespace andb
