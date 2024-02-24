using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

using Exceltk.Reader;

namespace Exceltk{
    public static class MarkDownExtension {
        public static SimpleTable ToMd(this string xls, string sheet) {
            FileStream stream=File.Open(xls, FileMode.Open, FileAccess.Read);
            IExcelDataReader excelReader=null;
            if (Path.GetExtension(xls)==".xls") {
                excelReader=ExcelReaderFactory.CreateBinaryReader(stream);
            } else if (Path.GetExtension(xls)==".xlsx") {
                excelReader=ExcelReaderFactory.CreateOpenXmlReader(stream);
            } else {
                throw new ArgumentException("Not Support Format: ");
            }
            DataSet dataSet=excelReader.AsDataSet();
            DataTable dataTable=dataSet.Tables[sheet];

            var table=new SimpleTable {
                    Name=dataTable.TableName,
                    Value=dataTable.ToMd(dataSet)
            };

            excelReader.Close();

            return table;
        }
        public static IEnumerable<SimpleTable> ToMd(this string xls) {
            FileStream stream=File.Open(xls, FileMode.Open, FileAccess.Read);
            IExcelDataReader excelReader=null;
            if (Path.GetExtension(xls)==".xls") {
                excelReader=ExcelReaderFactory.CreateBinaryReader(stream);
            } else if (Path.GetExtension(xls)==".xlsx") {
                excelReader=ExcelReaderFactory.CreateOpenXmlReader(stream);
            } else {
                throw new ArgumentException("Not Support Format: ");
            }
            DataSet dataSet=excelReader.AsDataSet();

            foreach (DataTable dataTable in dataSet.Tables) {
                var table=new SimpleTable {
                        Name=dataTable.TableName,
                        Value=dataTable.ToMd(dataSet)
                };

                yield return table;
            }

            excelReader.Close();
        }

        public static string ToMd(this DataTable table, DataSet dataSet, bool insertHeader=true) {
            table.Shrink();
            //table.RemoveColumnsByRow(0, string.IsNullOrEmpty);
            var sb=new StringBuilder();

            int i=0;
            foreach (DataRow row in table.Rows) {
                if (Config.BodyHead) {
                    if (i == 0 && insertHeader) {
                        sb.Append("|");
                        foreach (DataColumn col in table.Columns) {
                            sb.Append("|");
                        }
                        sb.Append(Environment.NewLine);

                        sb.Append("|");
                        foreach (DataColumn col in table.Columns) {
                            sb.Append(Config.TableAliginFormat).Append("|");
                        }
                        sb.Append(Environment.NewLine);
                    }
                }

                sb.Append("|");
                foreach (object cell in row.ItemArray) {
                    string value=GetCellValue(dataSet, cell);
                    if (i == 0 && Config.BodyHead) {
                        sb.AppendFormat("**{0}**",value).Append("|");
                    } else {
                        sb.Append(value).Append("|");
                    }
                }

                sb.Append(Environment.NewLine);

                if (!Config.BodyHead) {
                    if (i == 0 && insertHeader) {
                        sb.Append("|");
                        foreach (DataColumn col in table.Columns) {
                            sb.Append(Config.TableAliginFormat).Append("|");
                        }
                        sb.Append(Environment.NewLine);
                    }
                }

                i++;
            }
            return sb.ToString();
        }
        private static string GetCellValue(DataSet dataSet, object cell) {
            if (cell==null) {
                return "";
            }
            string value;
            var xlsCell=cell as XlsCell;
            if (xlsCell!=null) {
                value=xlsCell.GetMarkDownText(dataSet);
            } else {
                value=cell.ToString();
            }

            //Console.WriteLine(value);

            // Decimal precision
            if (Config.HasDecimalPrecision) {
                if (Regex.IsMatch(value, @"^(-?[0-9]{1,}[.][0-9]*)$")) {
                    var old=value;
                    if(Config.DecimalPrecision>0){
                        value = string.Format(Config.DecimalFormat, Double.Parse(value));
                    }else{
                        value = ((int)Double.Parse(value)).ToString();
                    }
                }
            }

            value = Regex.Replace(value, @"\r\n?|\n", "<br/>");
            value = value.Replace("|", "\\|");

            return value;
        }
    }
}