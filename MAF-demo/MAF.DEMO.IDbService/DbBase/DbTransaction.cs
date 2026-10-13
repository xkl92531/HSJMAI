using SqlSugar;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MAF.DEMO.IDbService.DbBase
{
    public class DbTransaction : IDisposable
    {
        ISqlSugarClient db;
        public DbTransaction(ISqlSugarClient db, IsolationLevel level = IsolationLevel.ReadCommitted) {
            // IsolationLevel.ReadCommitted
            //IsolationLevel.ReadUncommitted
            //IsolationLevel.RepeatableRead
            //IsolationLevel.Serializable
            this.db = db;
            db.AsTenant().BeginTran(level);
        }

        public void RollbackTran()
        {
            db.AsTenant().RollbackTran();
        }

        public void Complete() {
            db.AsTenant().CommitTran();
        }

        public void Dispose()
        {
            if (db != null)
            {
                db.AsTenant().RollbackTran();
            }
        }


    }
}
