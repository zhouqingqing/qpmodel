#ifdef __LATER
#ifndef __SELECTSTEMENT_H_
#define __SQLSTATEMENT_H_
namespace andb {
   struct SelectStatement : public SQLStatement
   {

    virtual LogicNode* CreatePlan (void) {
        return new LogicNode ();
    }

   };
}
#endif // __SELECTSSTATEMENT_H_
#endif /* __LATER */
