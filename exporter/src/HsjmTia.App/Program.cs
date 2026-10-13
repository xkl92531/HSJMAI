using System;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace HsjmTia.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            // 版本探测与 AssemblyResolve 装配推迟到用户选定工程之后（不同工程需要不同版本），
            // 因此这里不能碰任何 Openness 类型 —— 相关调用全在 MainForm 的 NoInlining 方法里。
            Application.Run(ShowMainForm());
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Form ShowMainForm() => new MainForm();
    }
}
