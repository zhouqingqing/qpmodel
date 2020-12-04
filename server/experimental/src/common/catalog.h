#pragma once
#include <string>
#include <utility>
#include <map>

#include "common/Statistics.h"
#include "common/common.h"
#include "common/dbcommon.h"

namespace andb {

// NOTE: None of the operations here are Multi-Thread (MT) safe.
// Tables are not aware of a SCHEMA, yet.
class SystemTable : public UseCurrentResource
{};

class SysTable : public SystemTable
{
    public:
    SysTable() {}

    bool CreateTable(std::string*             tabName,
                     std::vector<ColumnDef*>* columns,
                     std::string*             distBy = nullptr);

    TableDef*  TryTable(std::string* tblName);
    ColumnDef* Column(std::string* colName, std::string* tblName);

    void dropAllTables();

    private:
    std::map<std::string*, TableDef*, CaselessStringPtrCmp> records_;
};

class Catalog : public UseCurrentResource
{
    public:
    static void Init()
    {
        createOptimizerTestTables();
        createBuiltInTestTables();
    }

    static void DeInit()
    {
        Catalog::systable_->dropAllTables();
    }

    static void createOptimizerTestTables();
    static void createBuiltInTestTables();
    static int  Next();

    static RandomDevice rand_;
    static SysTable*    systable_;
    static SysStats*    sysstat_;
};
} // namespace andb
