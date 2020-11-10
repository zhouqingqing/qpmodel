#ifndef __IMPORTSTATEMENT_H_
#define __IMPORTSTATEMENT_H_
namespace andb {
struct ImportStatement : public SQLStatement {
    enum ImportType {
        kImportCSV,
        kImportTbl,  // Hyrise file format
        kImportBinary,
        kImportAuto
    };

    // Represents SQL Import statements.
        ImportStatement (ImportType type);
        ~ImportStatement () override;

        ImportType type;
        char* filePath;
        char* schema;
        char* tableName;

	virtual LogicNode* CreatePlan (void) { throw "illegal virtual call"; }
};
}
#endif // __IMPORTSTATEMENT_H_
