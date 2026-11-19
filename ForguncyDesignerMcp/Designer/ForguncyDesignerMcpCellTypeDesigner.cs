using ForguncyDesignerMcp.Designer.DrawingControl;
using GrapeCity.Forguncy.CellTypes;
using GrapeCity.Forguncy.Plugin;
using System.Windows;

namespace ForguncyDesignerMcp.Designer
{
    public class ForguncyDesignerMcpCellTypeDesigner : CellTypeDesigner<ForguncyDesignerMcpCellType>, IDefaultEditAction
    {
        public override FrameworkElement GetDrawingControl(ICellInfo cellInfo, IDrawingHelper drawingHelper)
        {
            BridgeHost.Schedule();
            return new ForguncyDesignerMcpCellTypeDrawingControl(this.CellType, cellInfo, drawingHelper);
        }

        public void OnStartEditStarting(IBuilderContext builderContext, StartEditingEventArgs args)
        {
            args.CancelEdit = true;
            BridgeHost.ShowManagement();
        }
    }
}
