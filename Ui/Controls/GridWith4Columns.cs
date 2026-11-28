using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace Lemora.Controls
{
    public class GridWith4Columns : Grid
    {
        public GridWith4Columns()
        {
            //ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star), SharedSizeGroup = $"{nameof(GridWith4Columns)}_{ColumnDefinitions.Count}" });
            //ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star), SharedSizeGroup = $"{nameof(GridWith4Columns)}_{ColumnDefinitions.Count}" });
            //ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star), SharedSizeGroup = $"{nameof(GridWith4Columns)}_{ColumnDefinitions.Count}" });
            //ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(1, GridUnitType.Star), SharedSizeGroup = $"{nameof(GridWith4Columns)}_{ColumnDefinitions.Count}" });
            ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(146, GridUnitType.Pixel) });
            ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(340, GridUnitType.Pixel) });
            ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto });
            ColumnDefinitions.Add(new ColumnDefinition() { Width = new GridLength(0, GridUnitType.Pixel) });
        }
    }
}
