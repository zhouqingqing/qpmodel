#ifndef __INSERTSTATEMENT_H_
#define __INSERTSTATEMENT_H_
namespace andb {
struct InsertStatement : public SQLStatement {

    virtual LogicNode* CreatePlan (void) {
        return new LogicNode ();
    }

};
}
#endif // __INSERTSTATEMENT_H_
