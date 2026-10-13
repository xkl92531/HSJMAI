using SqlSugar;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace HSJM.AI.IDbService.DbBase
{
    public class DbTransaction : IDisposable
    {
        ISqlSugarClient db;
        private bool _completed;
        public DbTransaction(ISqlSugarClient db, IsolationLevel level = IsolationLevel.ReadCommitted) {
            // IsolationLevel.ReadCommitted
            //IsolationLevel.ReadUncommitted
            //IsolationLevel.RepeatableRead
            //IsolationLevel.Serializable
            this.db = db;
            _completed = false;
            db.AsTenant().BeginTran(level);
        }

        public void RollbackTran()
        {
            if (!_completed)
            {
                db.AsTenant().RollbackTran();
                _completed = true;
            }
        }

        public void Complete() {
            db.AsTenant().CommitTran();
            _completed = true;
        }

        public void Dispose()
        {
            if (db != null && !_completed)
            {
                db.AsTenant().RollbackTran();
            }
        }


    }
}
