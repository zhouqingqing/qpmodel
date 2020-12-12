#include <string>
#include <utility>
#include <map>

#include "common/common.h"
#include "common/dbcommon.h"
#include "common/catalog.h"
#include "runtime/datum.h"


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
    TableDef*   tblDef = new TableDef(tabName, *columns);
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

    std::vector<std::string> colNames{
        "a1", "a2", "a3", "a4"
        , "b1", "b2", "b3", "b4"
        , "c1", "c2", "c3", "c4"
        , "d1", "d2", "d3", "d4" };
    ColumnType               colType{ SQLType::SQL_TYPE_INTEGER,
        TypeLenghts::lens_[(int)SQLType::SQL_TYPE_INTEGER] };
    for (int i = 0, j = 0; i < builtinTestTableNames.size(); ++i) {
        ColumnDef   cd1(static_cast<std::string*>(&colNames[j++]), colType, 0);
        ColumnDef   cd2(static_cast<std::string*>(&colNames[j++]), colType, 1);
        ColumnDef   cd3(static_cast<std::string*>(&colNames[j++]), colType, 2);
        ColumnDef   cd4(static_cast<std::string*>(&colNames[j++]), colType, 3);
        std::vector<ColumnDef*> cols{ &cd1, &cd2, &cd3, &cd4 };
        Catalog::systable_->CreateTable(static_cast<std::string*>(&builtinTestTableNames[i]),
                &cols);
        cd1.name_ = cd2.name_ = cd3.name_ = cd4.name_ = nullptr;
    }
}


void Catalog::populateOptimizerTestTables()
{
    /* std::vector<Datum> */
    Row r1(4), r2(4), r3(4), r4(4);
    r1[0] = 0, r1[1] = 1, r1[2] = 2, r1[3] = 3;
    r2[0] = 1, r2[1] = 2, r2[2] = 3, r2[3] = 4;
    r3[0] = 2, r3[1] = 3, r3[2] = 4, r3[3] = 5;
    r4[0] = 3, r4[1] = 4, r4[2] = 5, r4[3] = 6;

    std::vector<Row*> abcRows{ &r1, &r2, &r3, &r4 };
    int i = 0;
    while (i < builtinTestTableNames.size() - 2) {
        populateOneTable(builtinTestTableNames[i], abcRows);
        ++i;
    }

    // rows of d:
    // 0, 1,    2,    3 => r1
    // 1, 2, null,    4 => r2
    // 2, 2, null,    5 => r3
    // 3, 3,    5, null => r4

    r2[2] = NullFlag{};

    r3[1] = r3[0];
    r3[2] = NullFlag{};

    r4[1] = r4[0];
    r4[3] = NullFlag{};

    populateOneTable(builtinTestTableNames[i], abcRows);
}

void Catalog::populateOneTable(const std::string& tblName, const std::vector<Row*>& rows)
{
    TableDef *td = systable_->TryTable(const_cast<std::string*>(&tblName));

    if (!td)
        return;
    td->insertRows(const_cast<std::vector<Row*>&>(rows));
}
} // namespace andb
