using GrapeCity.Forguncy.CellTypes;
using System.Windows.Controls;

namespace ForguncyDesignerMcp.Designer.DrawingControl
{
public partial class ForguncyDesignerMcpCellTypeDrawingControl : System.Windows.Controls.UserControl
    {
        public ForguncyDesignerMcpCellTypeDrawingControl(ForguncyDesignerMcpCellType cellType, ICellInfo cellInfo, IDrawingHelper drawingHelper)
        {
            this.DataContext = new ForguncyDesignerMcpCellTypeDrawingControlViewModel(cellType, cellInfo, drawingHelper);

            InitializeComponent();
        }

        public class ForguncyDesignerMcpCellTypeDrawingControlViewModel
        {
            ForguncyDesignerMcpCellType _cellType;
            ICellInfo _cellInfo;
            IDrawingHelper _drawingHelper;
            public ForguncyDesignerMcpCellTypeDrawingControlViewModel(ForguncyDesignerMcpCellType cellType, ICellInfo cellInfo, IDrawingHelper drawingHelper)
            {
                _cellType = cellType;
                _cellInfo = cellInfo;
                _drawingHelper = drawingHelper;
            }
            public string Text { get => _cellType.ToString(); }
        }
    }
}
