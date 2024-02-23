using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Xml;
using System.Text;
using Exceltk.Reader.Xml;

namespace Exceltk.Reader {
    public class ExcelOpenXmlReader : IExcelDataReader {
        #region Members

        private const string COLUMN = "Column";
        private readonly List<int> m_defaultDateTimeStyles;
        private bool disposed;
        private object[] m_cellsValues;
        private int m_depth;
        private int m_emptyRowCount;
        private string m_exceptionMessage;
        private string m_instanceId = Guid.NewGuid().ToString();
        private bool m_isClosed;
        private bool m_isValid;

        private string m_namespaceUri;
        private object[] m_savedCellsValues;
        private Stream m_sheetStream;
        private XlsxWorkbook m_workbook;
        private XmlReader m_xmlReader;
        private ZipWorker m_zipWorker;

        #endregion

        #region ctor
        internal ExcelOpenXmlReader() {
            m_isValid = true;
            //m_isFirstRead = true;

            m_defaultDateTimeStyles = new List<int>(new[]{
                14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47
            });
        }
        #endregion

        #region IExcelDataReader Members

        public void Open(Stream fileStream) {
            m_zipWorker = new ZipWorker();
            m_zipWorker.Extract(fileStream);

            if (!m_zipWorker.IsValid) {
                m_isValid = false;
                m_exceptionMessage = m_zipWorker.ExceptionMessage;
                Dispose();
            } else {
                m_isValid = true;
                ReadGlobals();
            }
        }

        public DataSet AsDataSet() {
            if (!m_isValid) {
                return null;
            } else {
                return ReadDataSet();
            }
        }

        public void Close() {

            if (m_isClosed) {
                return;
            }
            m_isClosed = true;

            if (m_xmlReader != null) {
                m_xmlReader.Close();
                m_xmlReader = null;
            }

            if (m_sheetStream != null) {
                m_sheetStream.Close();
                m_sheetStream = null;
            }

            if (m_zipWorker != null) {
                m_zipWorker.Dispose();
                m_zipWorker = null;
            }
        }

        #endregion

        #region Implement

        private void ReadGlobals() {
            m_workbook = new XlsxWorkbook(
                m_zipWorker.GetWorkbookStream(),
                m_zipWorker.GetWorkbookRelsStream(),
                m_zipWorker.GetSharedStringsStream(),
                m_zipWorker.GetStylesStream());

            CheckDateTimeNumFmts(m_workbook.Styles.NumFmts);
        }

        private void CheckDateTimeNumFmts(List<XlsxNumFmt> list) {
            if (list.Count == 0) {
                return;
            }

            foreach (XlsxNumFmt numFmt in list) {
                if (string.IsNullOrEmpty(numFmt.FormatCode)) {
                    continue;
                }
                string fc = numFmt.FormatCode.ToLower();

                int pos;
                while ((pos = fc.IndexOf('"')) > 0) {
                    int endPos = fc.IndexOf('"', pos + 1);

                    if (endPos > 0) {
                        fc = fc.Remove(pos, endPos - pos + 1);
                    }
                }

                //it should only detect it as a date if it contains
                //dd mm mmm yy yyyy
                //h hh ss
                //AM PM
                //and only if these appear as "words" so either contained in [ ]
                //or delimted in someway
                //updated to not detect as date if format contains a #
                var formatReader = new FormatReader {
                    FormatString = fc
                };
                if (formatReader.IsDateFormatString()) {
                    m_defaultDateTimeStyles.Add(numFmt.Id);
                }
            }
        }

        private void ReadSheetGlobals(XlsxWorksheet sheet) {
            if (!ResetSheetReader(sheet)) {
                return;
            }

            //count rows and cols in case there is no dimension elements
            m_namespaceUri = null;
            int rows = 0;
            int cols = 0;
            int biggestColumn = 0;

            while (m_xmlReader.Read()) {
                if (m_xmlReader.NodeType == XmlNodeType.Element && m_xmlReader.LocalName == XlsxWorksheet.N_worksheet) {
                    //grab the namespaceuri from the worksheet element
                    m_namespaceUri = m_xmlReader.NamespaceURI;
                }

                if (m_xmlReader.NodeType == XmlNodeType.Element && m_xmlReader.LocalName == XlsxWorksheet.N_dimension) {
                    string dimValue = m_xmlReader.GetAttribute(XlsxWorksheet.A_ref);
                    sheet.Dimension = new XlsxDimension(dimValue);
                    break;
                }

                if (m_xmlReader.NodeType == XmlNodeType.Element && m_xmlReader.LocalName == XlsxWorksheet.N_row) {
                    rows++;
                }

                // check cells so we can find size of sheet if can't work it out from dimension or 
                // col elements (dimension should have been set before the cells if it was available)
                // ditto for cols
                if (sheet.Dimension == null && cols == 0 && m_xmlReader.NodeType == XmlNodeType.Element && m_xmlReader.LocalName == XlsxWorksheet.N_c) {
                    string refAttribute = m_xmlReader.GetAttribute(XlsxWorksheet.A_r);

                    if (refAttribute != null) {
                        int[] thisRef = refAttribute.ReferenceToColumnAndRow();
                        if (thisRef[1] > biggestColumn) {
                            biggestColumn = thisRef[1];
                        }
                    }
                }
            }

            // if we didn't get a dimension element then use the calculated rows/cols to create it
            if (sheet.Dimension == null) {
                if (cols == 0) {
                    cols = biggestColumn;
                }

                if (rows == 0 || cols == 0) {
                    sheet.IsEmpty = true;
                    return;
                }

                sheet.Dimension = new XlsxDimension(rows, cols);

                //we need to reset our position to sheet data
                if (!ResetSheetReader(sheet)) {
                    return;
                }
            }

            // read up to the sheetData element. if this element is empty then 
            // there aren't any rows and we need to null out dimension
            Debug.Assert(m_namespaceUri!=null);
            m_xmlReader.ReadToFollowing(XlsxWorksheet.N_sheetData, m_namespaceUri);
            if (m_xmlReader.IsEmptyElement) {
                sheet.IsEmpty=true;
            }                
        }

        private bool ResetSheetReader(XlsxWorksheet sheet) {
            if (m_sheetStream != null) {
                m_sheetStream.Close();
                m_sheetStream = null;
            }

            if (m_xmlReader != null) {
                m_xmlReader.Close();
                m_xmlReader = null;
            }

            m_sheetStream = m_zipWorker.GetWorksheetStream(sheet.Path);
            if (null == m_sheetStream) {
                return false;
            }

            m_xmlReader = XmlReader.Create(m_sheetStream);
            if (null == m_xmlReader) {
                return false;
            }

            return true;
        }

        private HyperLinkIndex ReadHyperLinkFormula(string thisSheetName, string formula){
            var sb = new StringBuilder();
            var f = formula.Substring(10);

            //HYPERLINK(#REF!,RIGHT(#REF!,3))
            //HYPERLINK(#REF!,SUBSTITUTE(#REF!,"https://coding.net/u/",""))
            //Console.WriteLine(formula);
            if(formula.StartsWith("HYPERLINK(#REF!")){
                //var begin = formula.IndexOf("\"");
                //var rest = formula.Substring(begin);
                //var end = rest.IndexOf("\"");
                //var h = rest.Substring(0,end);
                //Console.WriteLine(h);
                return null;
            }

            for(var i=0;i<f.Length;i++){
                var c = f[i];
                
                if(c==','){
                    var link = sb.ToString();
                    var pos = link.IndexOf("!");

                    var sheetName = "";
                    int col = 0;
                    int row = 0;
                    if(pos>=0){
                        Console.WriteLine(pos);
                        Console.WriteLine(link);
                        sheetName = link.Substring(0, pos);
                        var cellName = link.Substring(pos+1);
                        XlsxDimension.XlsxDim(cellName, out col, out row);

                    }else{
                        sheetName = thisSheetName;
                        var cellName = link.ToString();
                        //Console.WriteLine(cellName);
                        XlsxDimension.XlsxDim(cellName, out col, out row);
                    }

                    return new HyperLinkIndex(){
                        Sheet = sheetName,
                        Col = col,
                        Row = row
                    };
                }

                sb.Append(c);
            }
            return null;
        }

        private bool ReadSheetRow(XlsxWorksheet sheet) {
            if (sheet.ColumnsCount < 0) {
                return false;
            }

            if (null == m_xmlReader) {
                return false;
            }

            if (m_emptyRowCount != 0) {
                m_cellsValues = new object[sheet.ColumnsCount];
                m_emptyRowCount--;
                m_depth++;

                return true;
            }

            if (m_savedCellsValues != null) {
                m_cellsValues = m_savedCellsValues;
                m_savedCellsValues = null;
                m_depth++;

                return true;
            }

            bool isRow = false;
            bool isSheetData = (m_xmlReader.NodeType == XmlNodeType.Element &&
                                m_xmlReader.LocalName == XlsxWorksheet.N_sheetData);
            if (isSheetData) {
                isRow = m_xmlReader.ReadToFollowing(XlsxWorksheet.N_row, m_namespaceUri);
            } else {
                if (m_xmlReader.LocalName == XlsxWorksheet.N_row && m_xmlReader.NodeType == XmlNodeType.EndElement) {
                    //Console.WriteLine("read");
                    m_xmlReader.Read();
                }
                isRow = (m_xmlReader.NodeType == XmlNodeType.Element && m_xmlReader.LocalName == XlsxWorksheet.N_row);
            }

            if (!isRow) {
                return false;
            }

            //Console.WriteLine("New Row");

            m_cellsValues = new object[sheet.ColumnsCount];
            if (sheet.ColumnsCount > 13) {
                int i = sheet.ColumnsCount;
            }

            var rowIndexText = m_xmlReader.GetAttribute(XlsxWorksheet.A_r);
            Debug.Assert(rowIndexText!=null);
            int rowIndex=int.Parse(rowIndexText);

            if (rowIndex != (m_depth + 1)) {
                m_emptyRowCount = rowIndex - m_depth - 1;
            }

            bool hasValue = false;
            bool hasFormula = false;
            HyperLinkIndex hyperlinkIndex = null;
            string a_s = String.Empty;
            string a_t = String.Empty;
            string a_r = String.Empty;
            string f = String.Empty;
            int col = 0;
            int row = 0;

            while (m_xmlReader.Read()) {

                //Console.WriteLine("m_xmlReader.LocalName:{0}",m_xmlReader.LocalName);
                //Console.WriteLine("m_xmlReader.Value:{0}",m_xmlReader.Value);
                if (m_xmlReader.Depth == 2) {
                    break;
                }

                if (m_xmlReader.NodeType == XmlNodeType.Element) {
                    hasValue = false;

                    if (m_xmlReader.LocalName == XlsxWorksheet.N_c) {
                        a_s = m_xmlReader.GetAttribute(XlsxWorksheet.A_s);
                        a_t = m_xmlReader.GetAttribute(XlsxWorksheet.A_t);
                        a_r = m_xmlReader.GetAttribute(XlsxWorksheet.A_r);
                        XlsxDimension.XlsxDim(a_r, out col, out row);
                    } else if(m_xmlReader.LocalName == XlsxWorksheet.N_f){
                        hasFormula = true;
                    } else if (m_xmlReader.LocalName == XlsxWorksheet.N_v || m_xmlReader.LocalName == XlsxWorksheet.N_t) {
                        hasValue = true;
                        hasFormula = false;
                    } else {
                        //Console.WriteLine("m_xmlReader.LocalName:{0}",m_xmlReader.LocalName);
                        // Ignore
                    }
                }

                bool hasHyperLinkFormula = false;
                if(m_xmlReader.NodeType == XmlNodeType.Text && hasFormula){
                    string formula = m_xmlReader.Value.ToString();
                    if(formula.StartsWith("HYPERLINK(")){
                        hyperlinkIndex = this.ReadHyperLinkFormula(sheet.Name, formula);
                    }
                }
                

                if (m_xmlReader.NodeType == XmlNodeType.Text && hasValue) {
                    double number;
                    object o = m_xmlReader.Value;

                    //Console.WriteLine("O:{0}", o);

                    if (double.TryParse(o.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out number)) {
                        // numeric
                        o=number;
                    }

                    if (null!=a_t&&a_t==XlsxWorksheet.A_s) {
                        // string
                        var sstStr = m_workbook.SST[int.Parse(o.ToString())];
                        //Console.WriteLine(sstStr);
                        o=sstStr.ConvertEscapeChars();
                    } else if (null!=a_t&&a_t==XlsxWorksheet.N_inlineStr) {
                        // string inline
                        o=o.ToString().ConvertEscapeChars();
                    } else if (a_t=="b") {
                        // boolean
                        o=m_xmlReader.Value=="1";
                    } else if (a_t=="str") {
                        // string
                        o=m_xmlReader.Value;
                    } else if (null!=a_s) {
                        //something else
                        XlsxXf xf=m_workbook.Styles.CellXfs[int.Parse(a_s)];
                        if (xf.ApplyNumberFormat&&o!=null&&o.ToString()!=string.Empty&&
                            IsDateTimeStyle(xf.NumFmtId)) {
                            o=number.ConvertFromOATime();
                        } else if (xf.NumFmtId==49) {
                            o=o.ToString();
                        }
                    }

                    //Console.WriteLine(o);

                    if (col - 1 < m_cellsValues.Length) {
                        if(hyperlinkIndex!=null){
                            var co = new XlsCell(o);
                            co.HyperLinkIndex = hyperlinkIndex;
                            m_cellsValues[col - 1] = co;
                            hyperlinkIndex = null;
                        }else{
                            m_cellsValues[col - 1] = o;
                        }
                    } 
                }else{
                    //Console.WriteLine(m_xmlReader.Value.ToString());
                } 
            }

            if (m_emptyRowCount > 0) {
                //Console.WriteLine("Again");
                m_savedCellsValues = m_cellsValues;
                return ReadSheetRow(sheet);
            }
            m_depth++;

            return true;
        }

        private bool ReadHyperLinks(XlsxWorksheet sheet, DataTable table) {
            // ReadTo HyperLinks Node
            if (m_xmlReader == null) {
                return false;
            }

            m_xmlReader.ReadToFollowing(XlsxWorksheet.N_hyperlinks);
            if (m_xmlReader.IsEmptyElement) {
                return false;
            }

            // Read Realtionship Table
            Stream sheetRelStream = m_zipWorker.GetWorksheetRelsStream(sheet.Path);
            var hyperDict = new Dictionary<string, string>();
            if (sheetRelStream != null) {
                using (XmlReader reader = XmlReader.Create(sheetRelStream)) {
                    while (reader.Read()) {
                        if (reader.NodeType == XmlNodeType.Element && reader.LocalName == XlsxWorkbook.N_rel) {
                            string rid = reader.GetAttribute(XlsxWorkbook.A_id);
                            Debug.Assert(rid!=null);
                            hyperDict[rid] = reader.GetAttribute(XlsxWorkbook.A_target);
                        }
                    }
                    sheetRelStream.Close();
                }
            }


            // Read All HyperLink Node
            while (m_xmlReader.Read()) {
                if (m_xmlReader.NodeType != XmlNodeType.Element) {
                    break;
                }

                if (m_xmlReader.LocalName != XlsxWorksheet.N_hyperlink) {
                    break;
                }

                string aref = m_xmlReader.GetAttribute(XlsxWorksheet.A_ref);
                string display = m_xmlReader.GetAttribute(XlsxWorksheet.A_display);
                string rid = m_xmlReader.GetAttribute(XlsxWorksheet.A_rid);
                string location = m_xmlReader.GetAttribute("location"); // fragment identifier
                string hyperlink = display;


                Debug.Assert(rid!=null);
                if (hyperDict.ContainsKey(rid)) {
                    hyperlink = hyperDict[rid];
                }

                int col = -1;
                int row = -1;
                XlsxDimension.XlsxDim(aref, out col, out row);
                if (col >= 1 && row >= 1) {
                    row = row - 1;
                    col = col - 1;
                    if (row < table.Rows.Count) {
                        if (col < table.Rows[row].Count) {
                            object value = table.Rows[row][col];
                            var cell = value as XlsCell;
                            if(cell==null){
                                cell = new XlsCell(value);
                            }
                            //Console.WriteLine("H:{0}", hyperlink);
                            cell.SetHyperLink(hyperlink, location);
                            table.Rows[row][col] = cell;
                        }
                    }
                }
            }

            // Close
            m_xmlReader.Close();
            if (m_sheetStream != null) {
                m_sheetStream.Close();
            }

            return true;
        }

        private bool IsDateTimeStyle(int styleId) {
            return m_defaultDateTimeStyles.Contains(styleId);
        }

        private Dictionary<int, XlsxDimension> DetectDemension() {
            var dict = new Dictionary<int, XlsxDimension>();
            for (int sheetIndex = 0; sheetIndex < m_workbook.Sheets.Count; sheetIndex++) {
                XlsxWorksheet sheet = m_workbook.Sheets[sheetIndex];

                ReadSheetGlobals(sheet);

                if (sheet.Dimension != null) {
                    m_depth = 0;
                    m_emptyRowCount = 0;

                    // 
                    int detectRows = Math.Min(sheet.Dimension.LastRow, 100);
                    int maxColumnCount = 0;
                    while (detectRows > 0) {
                        ReadSheetRow(sheet);
                        maxColumnCount = Math.Max(LastIndexOfNonNull(m_cellsValues) + 1, maxColumnCount);
                        detectRows--;
                    }

                    // 
                    if (maxColumnCount < sheet.Dimension.LastCol) {
                        dict[sheetIndex] = new XlsxDimension(sheet.Dimension.LastRow, maxColumnCount);
                    } else {
                        dict[sheetIndex] = sheet.Dimension;
                    }
                } else {
                    dict[sheetIndex] = sheet.Dimension;
                }
            }
            return dict;
        }

        private static int LastIndexOfNonNull(object[] cellsValues) {
            for (int i = cellsValues.Length - 1; i >= 0; i--) {
                if (cellsValues[i] != null) {
                    return i;
                }
            }
            return 0;
        }

        private DataSet ReadDataSet() {
            var dataset = new DataSet();

            Dictionary<int, XlsxDimension> demensionDict = DetectDemension();

            for (int sheetIndex = 0; sheetIndex < m_workbook.Sheets.Count; sheetIndex++) {
                XlsxWorksheet sheet = m_workbook.Sheets[sheetIndex];
                var table = new DataTable(m_workbook.Sheets[sheetIndex].Name);

                ReadSheetGlobals(sheet);
                sheet.Dimension = demensionDict[sheetIndex];

                if (sheet.Dimension == null) {
                    continue;
                }

                m_depth = 0;
                m_emptyRowCount = 0;

                // Reada Columns
                for (int i = 0; i < sheet.ColumnsCount; i++) {
                    table.Columns.Add(i.ToString(CultureInfo.InvariantCulture), typeof(Object));
                }

                // Read Sheet Rows
                table.BeginLoadData();
                while (ReadSheetRow(sheet)) {
                    table.Rows.Add(m_cellsValues);
                }

                if (table.Rows.Count > 0) {
                    dataset.Tables.Add(table);
                }

                // Read HyperLinks
                ReadHyperLinks(sheet, table);

                table.EndLoadData();
            }
            dataset.AcceptChanges();
            dataset.FixDataTypes();
            return dataset;
        }

        #endregion

        #region IDispose

        public void Dispose() {
            Dispose(true);

            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing) {
            // Check to see if Dispose has already been called.

            if (!disposed) {
                if (disposing) {
                    if (m_xmlReader != null)
                        ((IDisposable)m_xmlReader).Dispose();
                    if (m_sheetStream != null)
                        m_sheetStream.Dispose();
                    if (m_zipWorker != null)
                        m_zipWorker.Dispose();
                }

                m_zipWorker = null;
                m_xmlReader = null;
                m_sheetStream = null;

                m_workbook = null;
                m_cellsValues = null;
                m_savedCellsValues = null;

                disposed = true;
            }
        }

        ~ExcelOpenXmlReader() {
            Dispose(false);
        }

        #endregion
    }
}