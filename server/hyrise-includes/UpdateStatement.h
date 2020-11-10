#ifndef __UPDATESTATEMENT_H_
#define __UPDATESTATEMENT_H_
namespace andb {
struct UpdateStatement : public SQLStatement {

    virtual LogicNode* CreatePlan (void) {
        return new LogicNode ();
    }

};
}
#endif // __UPDATESTATEMENT_H_
