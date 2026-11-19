using GrapeCity.Forguncy.CellTypes;
using GrapeCity.Forguncy.Plugin;
using System;
using System.ComponentModel;
using System.Linq;

namespace ForguncyDesignerMcp
{
    [Icon("pack://application:,,,/ForguncyDesignerMcp;component/Resources/Icon.png")]
    [Designer("ForguncyDesignerMcp.Designer.ForguncyDesignerMcpCellTypeDesigner, ForguncyDesignerMcp")]
    public class ForguncyDesignerMcpCellType : CellType
    {
        public ForguncyDesignerMcpCellType()
        {
            // Discovery also runs in published/runtime hosts; do not start a bridge there.
            if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "Forguncy")) return;
            try
            {
                GetType().Assembly.GetType("ForguncyDesignerMcp.Designer.BridgeHost", true)!
                    .GetMethod("Schedule")!.Invoke(null, null);
            }
            catch (Exception error) { System.Diagnostics.Trace.WriteLine("[ForguncyDesignerMcp] " + error.GetBaseException().Message); }
        }

        [DisplayName("说明")]
        public string MyProperty { get; set; } = "设计器 MCP 服务启动器；双击或使用托盘图标可查看状态和启停服务。";

        public override string ToString()
        {
            return "MCP 服务启动器";
        }
    }
}
