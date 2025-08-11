using iTextSharp.text;
using iTextSharp.text.pdf;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Data;
using System.Data.SqlClient;
using System.Drawing.Printing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web;
using System.Web.DynamicData;
using System.Xml.Linq;
// Avoid using System.Drawing directly if not needed
using DrawingFont = System.Drawing.Font;
using DrawingRectangle = System.Drawing.Rectangle;
using iTextFont = iTextSharp.text.Font;
using iTextRectangle = iTextSharp.text.Rectangle;

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

        private void CreateRequisitionDetails(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            // Create the first table - 2x2 grid (Date, Requested By, Invoice Company, Allocation Department)
            var topTable = new PdfPTable(4) { WidthPercentage = 100 };
            topTable.SetWidths(new float[] { 25f, 25f, 25f, 25f }); // Equal width columns

            // Row 1: Date and Requested By
            AddCell(topTable, "Date", normalFont, true);
            AddCell(topTable, doc.SavedTime.ToString("dd/MM/yyyy"), normalFont, true);
            AddCell(topTable, "Requested By", normalFont, true);
            AddCell(topTable, doc.RequestedByName, normalFont, true);

            document.Add(topTable);
            document.Add(new Paragraph(" ", new Font(Font.FontFamily.HELVETICA, 4)));// Add minimal spacing

            var topTable2 = new PdfPTable(4) { WidthPercentage = 100 };
            topTable.SetWidths(new float[] { 25f, 25f, 25f, 25f });

            // Row 2: Invoice Company and Allocation Department  
            AddCell(topTable2, "Invoice Company", normalFont, true);
            AddCell(topTable2, doc.CompanyName, normalFont, true);
            AddCell(topTable2, "Allocation Department", normalFont, true);
            AddCell(topTable2, doc.DepartmentName, normalFont, true);

            document.Add(topTable2);
            document.Add(new Paragraph(" ", new Font(Font.FontFamily.HELVETICA, 4)));// Add minimal spacing

            // Create the second table - Reason and Division Head row
            var middleTable = new PdfPTable(4) { WidthPercentage = 100 };
            middleTable.SetWidths(new float[] { 25f, 25f, 25f, 25f });

            AddCell(middleTable, "Reason", normalFont, true);
            AddCell(middleTable, doc.Reason, normalFont, true);
            AddCell(middleTable, "Division Head", normalFont, true);
            AddCell(middleTable, doc.DepartmentHeadName, normalFont, false);

            document.Add(middleTable);
            document.Add(new Paragraph(" ", new Font(Font.FontFamily.HELVETICA, 4)));

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
            bottomTable.SetWidths(new float[] { 20f, 40f, 20f, 20f });
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
            document.Add(new Paragraph(" ", new Font(Font.FontFamily.HELVETICA, 4)));
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
            topTable.SetWidths(new float[] { 25f, 25f, 25f, 25f });

            doc.EIDDateOfPurchase = Convert.ToDateTime(doc.EIDDateOfPurchase).ToShortDateString();
            AddCell(topTable, "Date of Purchase", normalFont, true);
            AddCell(topTable, doc.EIDDateOfPurchase, normalFont, true);
            AddCell(topTable, "Warranty", normalFont, true);
            AddCell(topTable, doc.EIDWarranty, normalFont, true);

            AddCell(topTable, "Make", normalFont, true);
            AddCell(topTable, doc.EIDMake, normalFont, true);
            AddCell(topTable, "Model", normalFont, true);
            AddCell(topTable, doc.EIDModel, normalFont, true);

            AddCell(topTable, "Serial Number", normalFont, true);
            AddCell(topTable, doc.EIDSerialNo, normalFont, true);
            AddCell(topTable, " ", normalFont, true);
            AddCell(topTable, " ", normalFont, true);

            document.Add(topTable);
            document.Add(new Paragraph(" ", new Font(Font.FontFamily.HELVETICA, 4)));
        }

        private void CreateCostSummaryTable(Document document, DocumentModel doc, Font headerFont, Font normalFont)
        {
            // Create main table with 8 columns to match the image layout
            var mainTable = new PdfPTable(8) { WidthPercentage = 100 };
            mainTable.SetWidths(new float[] { 24f, 10f, 10f, 15f, 20f, 8f, 12f, 15f });

            // First row - Main headers
            var configHeaderCell = new PdfPCell(new Phrase("Costing & Configuration (If repair only quotation will be attached)", headerFont));
            configHeaderCell.Colspan = 3;
            configHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            configHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            configHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(configHeaderCell);

            var costHeaderCell = new PdfPCell(new Phrase("Cost Summary & Recommended Supplier", headerFont));
            costHeaderCell.Colspan = 5;
            costHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            costHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            costHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(costHeaderCell);

            // Second row - Sub headers for left side and cost summary headers
            var descriptionHeaderCell = new PdfPCell(new Phrase("Description", headerFont));
            descriptionHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            descriptionHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            descriptionHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(descriptionHeaderCell);

            var attachedHeaderCell = new PdfPCell(new Phrase("Attached", headerFont));
            attachedHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            attachedHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            attachedHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(attachedHeaderCell);

            var notAttachedHeaderCell = new PdfPCell(new Phrase("Not Attached", headerFont));
            notAttachedHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            notAttachedHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            notAttachedHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(notAttachedHeaderCell);

            var supplierHeaderCell = new PdfPCell(new Phrase("Supplier", headerFont));
            supplierHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            supplierHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            supplierHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(supplierHeaderCell);

            var descriptionHeaderCell2 = new PdfPCell(new Phrase("Description", headerFont));
            descriptionHeaderCell2.HorizontalAlignment = Element.ALIGN_CENTER;
            descriptionHeaderCell2.VerticalAlignment = Element.ALIGN_MIDDLE;
            descriptionHeaderCell2.Border = Rectangle.BOX;
            mainTable.AddCell(descriptionHeaderCell2);

            var qtyHeaderCell = new PdfPCell(new Phrase("Qty", headerFont));
            qtyHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            qtyHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            qtyHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(qtyHeaderCell);

            var unitPriceHeaderCell = new PdfPCell(new Phrase("Unit Price", headerFont));
            unitPriceHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            unitPriceHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            unitPriceHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(unitPriceHeaderCell);

            var totalHeaderCell = new PdfPCell(new Phrase($"Total - {doc.Currency}", headerFont));
            totalHeaderCell.HorizontalAlignment = Element.ALIGN_CENTER;
            totalHeaderCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            totalHeaderCell.Border = Rectangle.BOX;
            mainTable.AddCell(totalHeaderCell);

            // Configuration rows
            string[] configItems = { "Quotation", "Configuration Evaluation", "Cost Breakdown" };
            bool?[] configValues = { doc.Quotation, doc.Configuration, doc.CostBreakdown };

            int maxRows = Math.Max(configItems.Length, doc.RequestedItems?.Count ?? 0);

            for (int i = 0; i < maxRows; i++)
            {
                // Left side - Configuration items
                if (i < configItems.Length)
                {
                    AddCell(mainTable, configItems[i], normalFont, false);

                    // Attached column
                    var attachedCell = new PdfPCell(new Phrase((configValues[i] == true) ? "✓" : "", normalFont));
                    attachedCell.HorizontalAlignment = Element.ALIGN_CENTER;
                    attachedCell.VerticalAlignment = Element.ALIGN_MIDDLE;
                    attachedCell.Border = Rectangle.BOX;
                    mainTable.AddCell(attachedCell);

                    // Not Attached column  
                    var notAttachedCell = new PdfPCell(new Phrase((configValues[i] == false) ? "✓" : "", normalFont));
                    notAttachedCell.HorizontalAlignment = Element.ALIGN_CENTER;
                    notAttachedCell.VerticalAlignment = Element.ALIGN_MIDDLE;
                    notAttachedCell.Border = Rectangle.BOX;
                    mainTable.AddCell(notAttachedCell);

                    // Supplier column (empty for config items)
                    AddCell(mainTable, "", normalFont, false);
                }
                else
                {
                    // Empty cells for configuration section
                    for (int j = 0; j < 4; j++)
                    {
                        AddCell(mainTable, "", normalFont, false);
                    }
                }

                // Right side - Cost summary items
                if (i < (doc.RequestedItems?.Count ?? 0))
                {
                    var item = doc.RequestedItems[i];
                    var total = item.Qty * item.UnitPrice;
                    AddCell(mainTable,item.SupplierName, normalFont, false); //i want to add supplier name here,but when i add it here
                    //i mashed up the whole form (it totally went wrong even thought their is no programaticaly errors)
                    //plx fix it ,give me only modified codes
                    AddCell(mainTable, item.Description, normalFont, false);
                    AddCell(mainTable, item.Qty.ToString(), normalFont, false);
                    AddCell(mainTable, item.UnitPrice.ToString("N2"), normalFont, false);
                    AddCell(mainTable, total.ToString("N2"), normalFont, false);
                }
                else
                {
                    // Empty cells for cost summary section
                    for (int j = 0; j < 4; j++)
                    {
                        AddCell(mainTable, "", normalFont, false);
                    }
                }
            }

            // Add confirmation row
            var confirmationCell = new PdfPCell(new Phrase("Costing, Configuration & recommendation confirmed by", normalFont));
            confirmationCell.Colspan = 4;
            confirmationCell.HorizontalAlignment = Element.ALIGN_LEFT;
            confirmationCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            confirmationCell.Border = Rectangle.BOX;
            mainTable.AddCell(confirmationCell);

            // Total cost row
            var totalCostCell = new PdfPCell(new Phrase($"Total Cost - {doc.Currency} (without SSC)", normalFont));
            totalCostCell.Colspan = 3;
            totalCostCell.HorizontalAlignment = Element.ALIGN_RIGHT;
            totalCostCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            totalCostCell.Border = Rectangle.BOX;
            mainTable.AddCell(totalCostCell);

            var totalValueCell = new PdfPCell(new Phrase(doc.TotalCost.ToString("N2"), normalFont));
            totalValueCell.HorizontalAlignment = Element.ALIGN_RIGHT;
            totalValueCell.VerticalAlignment = Element.ALIGN_MIDDLE;
            totalValueCell.Border = Rectangle.BOX;
            mainTable.AddCell(totalValueCell);

            document.Add(mainTable);
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
