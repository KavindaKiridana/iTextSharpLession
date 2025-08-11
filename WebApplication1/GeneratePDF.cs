using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Web;
using System.Xml.Linq;
// Avoid using System.Drawing directly if not needed
using DrawingFont = System.Drawing.Font;
using DrawingRectangle = System.Drawing.Rectangle;
using iTextFont = iTextSharp.text.Font;
using iTextRectangle = iTextSharp.text.Rectangle;
using iTextSharp.text;
using iTextSharp.text.pdf;
using System.Web.DynamicData;

namespace WebApplication1
{
    public class GeneratePDF
    {
        SqlConnection sqlconn = new SqlConnection(ConfigurationManager.ConnectionStrings["ITAssetConn"].ConnectionString);

        public void GetPDF(int DocumentId)
        {
            try
            {
                // Get document data
                var documentData = GetDocumentData(DocumentId);
                if (documentData == null)
                {
                    throw new Exception("Document not found");
                }

                // Generate PDF
                var pdfBytes = CreatePDF(documentData);

                // Generate filename with serial number
                string serialNo = GenerateSerialNumber(documentData);
                string fileName = $"IT_Requisition_{serialNo.Replace("/", "_")}.pdf";

                // Send PDF to browser for download
                HttpContext.Current.Response.Clear();
                HttpContext.Current.Response.ContentType = "application/pdf";
                HttpContext.Current.Response.AddHeader("Content-Disposition", $"attachment; filename={fileName}");
                HttpContext.Current.Response.BinaryWrite(pdfBytes);
                //HttpContext.Current.Response.End();
                HttpContext.Current.ApplicationInstance.CompleteRequest();
            }
            catch (Exception ex)
            {
                throw new Exception($"Error generating PDF: {ex.Message}", ex);
            }
        }

        private DocumentModel GetDocumentData(int documentId)
        {
            DocumentModel doc = null;

            try
            {
                sqlconn.Open();

                string query = @"
                    SELECT 
                        d.*,
                        c.CName as CompanyName,
                        c.Flag as CompanyFlag,
            r.RName as ReasonName,
                        dept.DName as DepartmentName,
                        usedDept.DName as UsedByDepartmentName,
                        u.FullName as RequestedByName,
                        deptHead.FullName as DepartmentHeadName,
                        t.IsMDSign,
                        itManager.FullName as ITManagerName,
                        ceo.FullName as CEOName,
                        md.FullName as MDName
                    FROM Document d
                    INNER JOIN Company c ON d.CompanyId = c.CompanyId
inner join Reason r on d.ReasonId = r.ReasonId
                    INNER JOIN Department dept ON d.DepartmentId = dept.DepartmentId
                    INNER JOIN Department usedDept ON d.UsedByToWhom = usedDept.DepartmentId
                    INNER JOIN Users u ON d.UsersId = u.UsersId
                    INNER JOIN Users deptHead ON d.DepartmentHead = deptHead.UsersId
                    INNER JOIN Template t ON d.TemplateId = t.TemplateId
                    INNER JOIN Users itManager ON t.ITManagerId = itManager.UsersId
                    INNER JOIN Users ceo ON t.CEOId = ceo.UsersId
                    LEFT JOIN Users md ON t.MDId = md.UsersId
                    WHERE d.DocumentId = @DocumentId";

                using (SqlCommand cmd = new SqlCommand(query, sqlconn))
                {
                    cmd.Parameters.AddWithValue("@DocumentId", documentId);

                    using (SqlDataReader reader = cmd.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            doc = new DocumentModel
                            {
                                DocumentId = documentId,
                                SavedTime = Convert.ToDateTime(reader["SavedTime"]),
                                CompanyName = reader["CompanyName"].ToString(),
                                CompanyFlag = reader["CompanyFlag"].ToString(),
                                Reason = reader["ReasonName"].ToString(),
                                DepartmentName = reader["DepartmentName"].ToString(),
                                UsedByDepartmentName = reader["UsedByDepartmentName"].ToString(),
                                RequestedByName = reader["RequestedByName"].ToString(),
                                DepartmentHeadName = reader["DepartmentHeadName"].ToString(),
                                Budgeted = Convert.ToBoolean(reader["Budgeted"]),
                                TotalCost = reader["TotalCost"] == DBNull.Value ? 0 : Convert.ToDecimal(reader["TotalCost"]),
                                ITDivisionComment = reader["ITDivisionComment"].ToString(),
                                ITDivisionRecommendation = reader["ITDivisionRecommendation"] == DBNull.Value ? null : reader["ITDivisionRecommendation"].ToString(),
                                Remarks = reader["Remarks"] == DBNull.Value ? null : reader["Remarks"].ToString(),
                                // EIDDateOfPurchase = reader["EIDDateOfPurchase"] == DBNull.Value ? (DateTime?)null : Convert.ToDateTime(reader["EIDDateOfPurchase"]),
                                EIDDateOfPurchase = reader["EIDDateOfPurchase"] == DBNull.Value ? "N/A" : Convert.ToDateTime(reader["EIDDateOfPurchase"]).ToString(),
                                EIDMake = reader["EIDMake"] == DBNull.Value ? null : reader["EIDMake"].ToString(),
                                EIDSerialNo = reader["EIDSerialNo"] == DBNull.Value ? null : reader["EIDSerialNo"].ToString(),
                                EIDWarranty = reader["EIDWarranty"] == DBNull.Value ? null : reader["EIDWarranty"].ToString(),
                                EIDModel = reader["EIDModel"] == DBNull.Value ? null : reader["EIDModel"].ToString(),
                                Quotation = reader["Quotation"] == DBNull.Value ? (bool?)null : Convert.ToBoolean(reader["Quotation"]),
                                Configuration = reader["Configuration"] == DBNull.Value ? (bool?)null : Convert.ToBoolean(reader["Configuration"]),
                                CostBreakdown = reader["CostBeakdown"] == DBNull.Value ? (bool?)null : Convert.ToBoolean(reader["CostBeakdown"]),
                                IsMDSign = Convert.ToBoolean(reader["IsMDSign"]),
                                ITManagerName = reader["ITManagerName"].ToString(),
                                CEOName = reader["CEOName"].ToString(),
                                MDName = reader["MDName"] == DBNull.Value ? null : reader["MDName"].ToString()
                            };
                        }
                    }
                }

                // Get requested items
                if (doc != null)
                {
                    doc.RequestedItems = GetRequestedItems(documentId);
                    doc.Currency = GetCurrency(documentId);
                }
            }
            finally
            {
                if (sqlconn.State == ConnectionState.Open)
                    sqlconn.Close();
            }

            return doc;
        }

        private List<RequestedItemModel> GetRequestedItems(int documentId)
        {
            var items = new List<RequestedItemModel>();

            string query = @"
                SELECT 
                    rip.Description,
                    rip.Qty,
                    rip.UnitPrice,
                    s.SName as SupplierName
                FROM RequestedItemPayments rip
                INNER JOIN Supplier s ON rip.SupplierId = s.SupplierId
                WHERE rip.DocumentID = @DocumentId
                ORDER BY rip.RequestedItemPaymentsId";

            using (SqlCommand cmd = new SqlCommand(query, sqlconn))
            {
                cmd.Parameters.AddWithValue("@DocumentId", documentId);

                using (SqlDataReader reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        items.Add(new RequestedItemModel
                        {
                            Description = reader["Description"].ToString(),
                            Qty = Convert.ToInt32(reader["Qty"]),
                            UnitPrice = Convert.ToDecimal(reader["UnitPrice"]),
                            SupplierName = reader["SupplierName"].ToString()
                        });
                    }
                }
            }

            return items;
        }


        private string GetCurrency(int documentId)
        {
            string currency = "LKR";

            string query = @"
                SELECT TOP 1 s.Currency
                FROM RequestedItemPayments rip
                INNER JOIN Supplier s ON rip.SupplierId = s.SupplierId
                WHERE rip.DocumentID = @DocumentId";

            using (SqlCommand cmd = new SqlCommand(query, sqlconn))
            {
                cmd.Parameters.AddWithValue("@DocumentId", documentId);

                var result = cmd.ExecuteScalar();
                if (result != null)
                {
                    currency = result.ToString();
                }
            }

            return currency;
        }

        private string GenerateSerialNumber(DocumentModel doc)
        {
            string year = doc.SavedTime.Year.ToString();
            string documentIdFormatted = doc.DocumentId.ToString("D4");
            return $"{doc.CompanyFlag}/{year}/{documentIdFormatted}";
        }

        private byte[] CreatePDF(DocumentModel doc)
        {
            using (var memoryStream = new MemoryStream())
            {
                Document document = new Document(PageSize.A4, 18, 18, 18, 18);
                PdfWriter writer = PdfWriter.GetInstance(document, memoryStream);

                document.Open();

                // Fonts
                var titleFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10);
                var headerFont = FontFactory.GetFont(FontFactory.HELVETICA_BOLD, 10);
                var normalFont = FontFactory.GetFont(FontFactory.HELVETICA, 10);
                var smallFont = FontFactory.GetFont(FontFactory.HELVETICA, 10);

                // Header
                CreateHeader(document, doc, titleFont, headerFont, normalFont);

                // Requisition Details
                CreateRequisitionDetails(document, doc, headerFont, normalFont);

                // Existing Item Details
                CreateExistingItemDetails(document, doc, headerFont, normalFont);

                // Cost Summary Table
                CreateCostSummaryTable(document, doc, headerFont, normalFont);

                // Comments and Recommendations
                CreateCommentsSection(document, doc, headerFont, normalFont);

                // Signatures
                CreateSignatureSection(document, doc, headerFont, normalFont);

                document.Close();
                return memoryStream.ToArray();
            }
        }

        private void CreateHeader(Document document, DocumentModel doc, Font titleFont, Font headerFont, Font normalFont)
        {
            // Create main header table with 3 columns
            var headerTable = new PdfPTable(3) { WidthPercentage = 100 };
            headerTable.SetWidths(new float[] { 30f, 40f, 30f }); // Left, Center, Right proportions

            // Left cell - Form number
            var leftCell = new PdfPCell();
            leftCell.Border = Rectangle.NO_BORDER;
            leftCell.HorizontalAlignment = Element.ALIGN_LEFT;
            leftCell.VerticalAlignment = Element.ALIGN_TOP;
            leftCell.AddElement(new Paragraph("Form:IT-PD-01.0", normalFont));

            // Center cell - Title and Company
            var centerCell = new PdfPCell();
            centerCell.Border = Rectangle.NO_BORDER;
            centerCell.HorizontalAlignment = Element.ALIGN_CENTER;
            centerCell.VerticalAlignment = Element.ALIGN_TOP;

            // Create center content with proper alignment
            var titleParagraph = new Paragraph("IT Approval Requisition form", titleFont);
            titleParagraph.Alignment = Element.ALIGN_CENTER;

            var companyParagraph = new Paragraph("Renuka Group", headerFont);
            companyParagraph.Alignment = Element.ALIGN_CENTER;

            centerCell.AddElement(titleParagraph);
            centerCell.AddElement(companyParagraph);

            // Right cell - Serial Number
            var rightCell = new PdfPCell();
            rightCell.Border = Rectangle.NO_BORDER;
            rightCell.HorizontalAlignment = Element.ALIGN_RIGHT;
            rightCell.VerticalAlignment = Element.ALIGN_TOP;
            rightCell.AddElement(new Paragraph($"Serial No:RGIT/2025/0001", normalFont));

            // Add cells to table
            headerTable.AddCell(leftCell);
            headerTable.AddCell(centerCell);
            headerTable.AddCell(rightCell);

            // Add table to document
            document.Add(headerTable);

            // Add some space after header
            //document.Add(new Paragraph(" ", normalFont));
        }

        //private void CreateRequisitionDetails(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        //{
        //    document.Add(new Paragraph("Requisition Details", headerFont));

        //    var table = new PdfPTable(4) { WidthPercentage = 100 };
        //    table.SetWidths(new float[] { 25f, 25f, 25f, 25f });

        //    // Row 1
        //    AddCell(table, "Date", normalFont, true);
        //    AddCell(table, doc.SavedTime.ToString("dd/MM/yyyy"), normalFont, false);
        //    AddCell(table, "Requested By", normalFont, true);
        //    AddCell(table, doc.RequestedByName, normalFont, false);

        //    // Row 2
        //    AddCell(table, "Invoice Company", normalFont, true);
        //    AddCell(table, doc.CompanyName, normalFont, false);
        //    AddCell(table, "Allocation Department", normalFont, true);
        //    AddCell(table, doc.DepartmentName, normalFont, false);

        //    // Row 3
        //    AddCell(table, "Division Head", normalFont, true);
        //    AddCell(table, doc.DepartmentHeadName, normalFont, false);
        //    AddCell(table, "Budgeted", normalFont, true);
        //    AddCell(table, doc.Budgeted ? "Yes" : "No", normalFont, false);

        //    // Row 4 - Requirement Items and Suppliers
        //    var itemsText = string.Join("\n", doc.RequestedItems.ConvertAll(x => x.Description));
        //    var suppliersText = string.Join("\n", doc.RequestedItems.ConvertAll(x => x.SupplierName).Distinct());

        //    AddCell(table, "Requirement (Item)", normalFont, true);
        //    AddCell(table, itemsText, normalFont, false);
        //    AddCell(table, "Used by / To whom", normalFont, true);
        //    AddCell(table, doc.UsedByDepartmentName, normalFont, false);

        //    // Row 5
        //    AddCell(table, "Purchase Order Supplier", normalFont, true);
        //    var supplierCell = new PdfPCell(new Phrase(suppliersText, normalFont));
        //    supplierCell.Colspan = 3;
        //    table.AddCell(supplierCell);

        //    document.Add(table);
        //}

        private void CreateRequisitionDetails(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            // Create the first table - 2x2 grid (Date, Requested By, Invoice Company, Allocation Department)
            var topTable = new PdfPTable(4) { WidthPercentage = 100 };
            topTable.SetWidths(new float[] { 25f, 25f,25f,25f }); // Equal width columns

            // Row 1: Date and Requested By
            AddCell(topTable, "Date", normalFont, true);
            AddCell(topTable, doc.SavedTime.ToString("dd/MM/yyyy"), normalFont, true);
            AddCell(topTable, "Requested By", normalFont, true);
            AddCell(topTable, doc.RequestedByName, normalFont, true);

            // Row 2: Invoice Company and Allocation Department  
            AddCell(topTable, "Invoice Company", normalFont, true);
            AddCell(topTable, doc.CompanyName, normalFont, true);
            AddCell(topTable, "Allocation Department", normalFont, true);
            AddCell(topTable, doc.DepartmentName, normalFont, true);

            document.Add(topTable);

            // Create the second table - Reason and Division Head row
            var middleTable = new PdfPTable(4) { WidthPercentage = 100 };
            middleTable.SetWidths(new float[] { 25f, 25f, 25f,25f }); 

            AddCell(middleTable, "Reason", normalFont, true);
            AddCell(middleTable, doc.Reason, normalFont, true);
            AddCell(middleTable, "Division Head", normalFont, true);
            AddCell(middleTable, doc.DepartmentHeadName, normalFont, false);

            document.Add(middleTable);

            // Create the main requisition details table
            var detailsTable = new PdfPTable(1) { WidthPercentage = 100 };

            // Title row spanning full width
            var titleCell = new PdfPCell(new Phrase("Requisition Details", headerFont));
            titleCell.HorizontalAlignment = Element.ALIGN_CENTER;
            //titleCell.Padding = 5f;
            detailsTable.AddCell(titleCell);

            document.Add(detailsTable);

            // Requirement Items and Suppliers
            var itemsText = string.Join(", ", doc.RequestedItems.ConvertAll(x => x.Description));
            var suppliersText = string.Join(", ", doc.RequestedItems.ConvertAll(x => x.SupplierName).Distinct());

            var bottomTable = new PdfPTable(4) { WidthPercentage = 100 };
            bottomTable.SetWidths(new float[] { 20f,40f,20f,20f});
            AddCell(bottomTable, "Requirement", normalFont, true);
            AddCell(bottomTable, itemsText, normalFont, true);
            AddCell(bottomTable, "Used by/To whom", normalFont, true);
            AddCell(bottomTable, doc.UsedByDepartmentName, normalFont, true);

            AddCell(bottomTable, "Supplier", normalFont, true);
            AddCell(bottomTable, suppliersText, normalFont, false);
            AddCell(bottomTable, "Budgeted", normalFont, true);
            AddCell(bottomTable, doc.Budgeted ? "Yes" : "No", normalFont, false);

            document.Add(bottomTable);
           
            // Add minimal spacing
            //document.Add(new Paragraph(" ", new Font(Font.FontFamily.HELVETICA, 4)));
        }


        private void CreateExistingItemDetails(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            // Create the main requisition details table
            var detailsTable = new PdfPTable(1) { WidthPercentage = 100 };
            // Title row spanning full width
            var titleCell = new PdfPCell(new Phrase("Existing Item Details (If the item is not a new/ new project)", headerFont));
            titleCell.HorizontalAlignment = Element.ALIGN_CENTER;
            //titleCell.Padding = 5f;
            detailsTable.AddCell(titleCell);
            document.Add(detailsTable);

            // Check each field individually and assign "N/A" if null
            if (doc.EIDDateOfPurchase == null) doc.EIDDateOfPurchase = "N/A";
            if (string.IsNullOrWhiteSpace(doc.EIDWarranty)) doc.EIDWarranty = "N/A";
            if (string.IsNullOrWhiteSpace(doc.EIDMake)) doc.EIDMake = "N/A";
            if (string.IsNullOrWhiteSpace(doc.EIDModel)) doc.EIDModel = "N/A";
            if (string.IsNullOrWhiteSpace(doc.EIDSerialNo)) doc.EIDSerialNo = "N/A";

            var topTable = new PdfPTable(4) { WidthPercentage = 100 };
            topTable.SetWidths(new float[] { 25f, 25f, 25f, 25f }); // Equal width columns

            AddCell(topTable, "Date of Purchase", normalFont, true);
            AddCell(topTable, doc.EIDDateOfPurchase, normalFont, true);
            AddCell(topTable, "Warranty", normalFont, true);
            AddCell(topTable, doc.EIDWarranty, normalFont, true);

            AddCell(topTable, "Make", normalFont, true);
            AddCell(topTable,doc.EIDMake, normalFont, true);
            AddCell(topTable, "Model", normalFont, true);
            AddCell(topTable, doc.EIDModel, normalFont, true);

            AddCell(topTable, "Serial Number", normalFont, true);
            AddCell(topTable, doc.EIDSerialNo, normalFont, true);
            AddCell(topTable, " ", normalFont, true);
            AddCell(topTable, " ", normalFont, true);

            document.Add(topTable);
        }

        private void CreateCostSummaryTable(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            //var detailsTable = new PdfPTable(2) { WidthPercentage = 100 };
            //// Title row spanning full width
            //var titleCell = new PdfPCell(new Phrase("Existing Item Details (If the item is not a new/ new project)", headerFont));
            //titleCell.HorizontalAlignment = Element.ALIGN_CENTER;
            //detailsTable.AddCell(titleCell);
            //document.Add(detailsTable);

            //document.Add(new Paragraph("Costing & Configuration (If repair only quotation will be attached)", headerFont));

            // Configuration options
            var configTable = new PdfPTable(3) { WidthPercentage = 50 };

            AddCell(configTable, "Quotation", normalFont, true);
            AddCell(configTable, GetBooleanDisplay(doc.Quotation), normalFont, false);
            AddCell(configTable, "Configuration Evaluation", normalFont, true);
            AddCell(configTable, GetBooleanDisplay(doc.Configuration), normalFont, false);
            AddCell(configTable, "Cost Breakdown", normalFont, true);
            AddCell(configTable, GetBooleanDisplay(doc.CostBreakdown), normalFont, false);

            document.Add(configTable);
            document.Add(new Paragraph(" ", normalFont));

            // Cost Summary Table
            document.Add(new Paragraph("Cost Summary & Recommended Supplier", headerFont));

            var costTable = new PdfPTable(5) { WidthPercentage = 100 };
            costTable.SetWidths(new float[] { 20f, 30f, 15f, 15f, 20f });

            // Header
            AddCell(costTable, "Supplier", normalFont, true);
            AddCell(costTable, "Description", normalFont, true);
            AddCell(costTable, "Qty", normalFont, true);
            AddCell(costTable, "Unit Price", normalFont, true);
            AddCell(costTable, $"Total - {doc.Currency}", normalFont, true);

            // Data rows
            foreach (var item in doc.RequestedItems)
            {
                var total = item.Qty * item.UnitPrice;
                AddCell(costTable, item.SupplierName, normalFont, false);
                AddCell(costTable, item.Description, normalFont, false);
                AddCell(costTable, item.Qty.ToString(), normalFont, false);
                AddCell(costTable, item.UnitPrice.ToString("N2"), normalFont, false);
                AddCell(costTable, total.ToString("N2"), normalFont, false);
            }

            // Total row
            var totalCell = new PdfPCell(new Phrase($"Total Cost - {doc.Currency} (without SSC): {doc.TotalCost:N2}", headerFont));
            totalCell.Colspan = 5;
            totalCell.HorizontalAlignment = Element.ALIGN_RIGHT;
            costTable.AddCell(totalCell);

            document.Add(costTable);
            document.Add(new Paragraph(" ", normalFont));
        }

        private void CreateCommentsSection(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            document.Add(new Paragraph("IT Division Comments", headerFont));
            document.Add(new Paragraph(doc.ITDivisionComment ?? "", normalFont));
            document.Add(new Paragraph(" ", normalFont));

            if (!string.IsNullOrEmpty(doc.ITDivisionRecommendation))
            {
                document.Add(new Paragraph("IT Division Recommendation (with justification)", headerFont));
                document.Add(new Paragraph(doc.ITDivisionRecommendation, normalFont));
                document.Add(new Paragraph(" ", normalFont));
            }

            if (!string.IsNullOrEmpty(doc.Remarks))
            {
                document.Add(new Paragraph("Remarks", headerFont));
                document.Add(new Paragraph(doc.Remarks, normalFont));
                document.Add(new Paragraph(" ", normalFont));
            }
        }

        private void CreateSignatureSection(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            var sigTable = new PdfPTable(doc.IsMDSign ? 3 : 2) { WidthPercentage = 100 };

            // IT Manager
            var itCell = new PdfPCell();
            itCell.Border = Rectangle.NO_BORDER;
            itCell.AddElement(new Paragraph("Manager IT", headerFont));
            itCell.AddElement(new Paragraph(doc.ITManagerName, normalFont));
            itCell.AddElement(new Paragraph(" ", normalFont));
            itCell.AddElement(new Paragraph("Signature", normalFont));
            sigTable.AddCell(itCell);

            // CEO
            var ceoCell = new PdfPCell();
            ceoCell.Border = Rectangle.NO_BORDER;
            ceoCell.AddElement(new Paragraph("CEO", headerFont));
            ceoCell.AddElement(new Paragraph(doc.CEOName, normalFont));
            ceoCell.AddElement(new Paragraph(" ", normalFont));
            ceoCell.AddElement(new Paragraph("Signature", normalFont));
            sigTable.AddCell(ceoCell);

            // MD (only if required)
            if (doc.IsMDSign && !string.IsNullOrEmpty(doc.MDName))
            {
                var mdCell = new PdfPCell();
                mdCell.Border = Rectangle.NO_BORDER;
                mdCell.AddElement(new Paragraph("MD", headerFont));
                mdCell.AddElement(new Paragraph(doc.MDName, normalFont));
                mdCell.AddElement(new Paragraph(" ", normalFont));
                mdCell.AddElement(new Paragraph("Signature", normalFont));
                sigTable.AddCell(mdCell);
            }

            document.Add(sigTable);
        }

        private void AddCell(PdfPTable table, string text, Font font, bool isHeader)
        {
            var cell = new PdfPCell(new Phrase(text, font));
            if (isHeader)
            {
                //cell.BackgroundColor = BaseColor.LIGHT_GRAY;
            }
            table.AddCell(cell);
        }

        private string GetBooleanDisplay(bool? value)
        {
            if (!value.HasValue) return "";
            return value.Value ? "Yes" : "No";
        }
    }

    // Data Models
    public class DocumentModel
    {
        public int DocumentId { get; set; }
        public DateTime SavedTime { get; set; }
        public string CompanyName { get; set; }
        public string CompanyFlag { get; set; }
        public string DepartmentName { get; set; }
        public string UsedByDepartmentName { get; set; }
        public string RequestedByName { get; set; }
        public string DepartmentHeadName { get; set; }
        public bool Budgeted { get; set; }
        public decimal TotalCost { get; set; }
        public string ITDivisionComment { get; set; }
        public string ITDivisionRecommendation { get; set; }
        public string Remarks { get; set; }
        // public DateTime? EIDDateOfPurchase { get; set; }
        public string EIDDateOfPurchase { get; set; }
        public string EIDMake { get; set; }
        public string EIDSerialNo { get; set; }
        public string EIDWarranty { get; set; }
        public string EIDModel { get; set; }
        public bool? Quotation { get; set; }
        public bool? Configuration { get; set; }
        public bool? CostBreakdown { get; set; }
        public bool IsMDSign { get; set; }
        public string ITManagerName { get; set; }
        public string CEOName { get; set; }
        public string MDName { get; set; }
        public List<RequestedItemModel> RequestedItems { get; set; } = new List<RequestedItemModel>();
        public string Currency { get; set; } = "LKR";
        public String Reason { get; set; }
    }

    public class RequestedItemModel
    {
        public string Description { get; set; }
        public int Qty { get; set; }
        public decimal UnitPrice { get; set; }
        public string SupplierName { get; set; }
    }
}
