#ifndef __EXPORTSTATEMENT_H_
#define __EXPORTSTATEMENT_H_
namespace andb {
struct ExportStatement : public SQLStatement {
    ExportStatement (ImportType type);
    ~ExportStatement () override;

    // ImportType is used for compatibility reasons
    ImportType type;
    char* filePath;
    char* schema;
    char* tableName;

    virtual LogicNode* CreatePlan (void) { throw "illegal virtual call"; }
};
}
#endif // __EXPORTSTATEMENT_H_
