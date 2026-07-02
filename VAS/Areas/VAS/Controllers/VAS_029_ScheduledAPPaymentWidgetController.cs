using System;
using System.Data;
using System.Globalization;
using System.Web.Mvc;
using VAdvantage.Classes;
using VAdvantage.DataBase;
using VAdvantage.Model;
using VAdvantage.Utility;
using VIS.Filters;

namespace VAS.Controllers
{
    /// <summary>
    /// Module Name : VAS Dashboard
    /// Purpose     : Returns the total scheduled AP payment amount
    ///               due during the current week.
    /// </summary>
    /*
     * Labels / Message Keys
     * 1 | Scheduled                       | VAS_029_MessageScheduled
     * 2 | Why                             | VAS_029_MessageWhy
     * 3 | Scheduled for payment this week | VAS_029_MessageScheduledForPaymentThisWeek
     * 4 | Failed to load scheduled amount | VAS_029_MessageLoadError
     */
    public class VAS_029_ScheduledAPPaymentWidgetController : Controller
    {
        [AjaxAuthorizeAttribute]
        [AjaxSessionFilterAttribute]
        public JsonResult GetScheduledAPPaymentThisWeek()
        {
            if (Session["ctx"] == null)
            {
                return Json(new
                {
                    success = false,
                    error = "Session Expired",
                    errorText = "Session Expired",
                    hasData = false
                }, JsonRequestBehavior.AllowGet);
            }

            Ctx ctx = Session["ctx"] as Ctx;

            if (ctx == null)
            {
                return Json(new
                {
                    success = false,
                    error = "Session Expired",
                    errorText = "Session Expired",
                    hasData = false
                }, JsonRequestBehavior.AllowGet);
            }

            IDataReader dr = null;

            try
            {
                DateTime today = DateTime.Today;

                /*
                 * Current week:
                 * Monday inclusive through next Monday exclusive.
                 */
                int daysFromMonday = ((int)today.DayOfWeek + 6) % 7;

                DateTime weekFrom = today
                    .AddDays(-daysFromMonday)
                    .Date;

                DateTime weekTo = weekFrom.AddDays(7);

                int adClientId = ctx.GetAD_Client_ID();

                string invoiceBaseSql = @"
                    SELECT Invoice.C_Invoice_ID,
                           Invoice.AD_Client_ID,
                           Invoice.AD_Org_ID,
                           Invoice.C_Currency_ID,
                           Invoice.DateAcct,
                           Invoice.C_ConversionType_ID,
                           Invoice.IsReturnTrx
                    FROM C_Invoice Invoice
                    WHERE Invoice.IsActive = 'Y'
                    AND Invoice.AD_Client_ID = " +
                    adClientId.ToString(CultureInfo.InvariantCulture) + @"
                    AND Invoice.IsSOTrx = 'N'
                    AND Invoice.DocStatus IN ('CO', 'CL')";

                /*
                 * Apply role security only to the main physical table.
                 */
                invoiceBaseSql = MRole.GetDefault(ctx).AddAccessSQL(
                    invoiceBaseSql,
                    "Invoice",
                    MRole.SQL_FULLYQUALIFIED,
                    MRole.SQL_RO
                );

                string dateFilter = GetDateFilter(
                    "InvoicePaySchedule.DueDate",
                    weekFrom,
                    weekTo
                );

                string sql = @"
                    WITH SchemaCurrency AS
                    (
                        SELECT ClientInfo.AD_Client_ID,
                               AcctSchema.C_Currency_ID,
                               Currency.StdPrecision,
                               Currency.ISO_Code,
                               CASE
                                   WHEN Currency.CurSymbol IS NOT NULL
                                       THEN Currency.CurSymbol
                                   ELSE Currency.ISO_Code
                               END AS CurSymbol
                        FROM AD_ClientInfo ClientInfo
                        INNER JOIN C_AcctSchema AcctSchema
                            ON (ClientInfo.C_AcctSchema1_ID = AcctSchema.C_AcctSchema_ID)
                        INNER JOIN C_Currency Currency
                            ON (AcctSchema.C_Currency_ID = Currency.C_Currency_ID)
                        WHERE ClientInfo.IsActive = 'Y'
                        AND ClientInfo.AD_Client_ID = " +
                        adClientId.ToString(CultureInfo.InvariantCulture) + @"
                    ),
                    InvoiceData AS
                    (
                        " + invoiceBaseSql + @"
                    ),
                    ScheduledData AS
                    (
                        SELECT InvoiceData.AD_Client_ID,
                               SchemaCurrency.C_Currency_ID,
                               SchemaCurrency.ISO_Code,
                               SchemaCurrency.CurSymbol,
                               SchemaCurrency.StdPrecision,
                               CASE
                                   WHEN COALESCE(InvoiceData.IsReturnTrx, 'N') = 'Y'
                                       THEN -1
                                   ELSE 1
                               END *
                               CASE
                                   WHEN InvoiceData.C_Currency_ID =
                                        SchemaCurrency.C_Currency_ID
                                       THEN COALESCE(
                                           InvoicePaySchedule.DueAmt,
                                           0
                                       )
                                   ELSE CurrencyConvert(
                                       COALESCE(
                                           InvoicePaySchedule.DueAmt,
                                           0
                                       ),
                                       InvoiceData.C_Currency_ID,
                                       SchemaCurrency.C_Currency_ID,
                                       InvoiceData.DateAcct,
                                       InvoiceData.C_ConversionType_ID,
                                       InvoiceData.AD_Client_ID,
                                       InvoiceData.AD_Org_ID
                                   )
                               END AS ScheduledAmount
                        FROM InvoiceData InvoiceData
                        INNER JOIN C_InvoicePaySchedule InvoicePaySchedule
                            ON (InvoiceData.C_Invoice_ID =
                                InvoicePaySchedule.C_Invoice_ID)
                        INNER JOIN SchemaCurrency SchemaCurrency
                            ON (SchemaCurrency.AD_Client_ID =
                                InvoiceData.AD_Client_ID)
                        WHERE InvoicePaySchedule.IsActive = 'Y'
                        AND COALESCE(
                            InvoicePaySchedule.VA009_IsPaid,
                            'N'
                        ) <> 'Y'
                        AND COALESCE(
                            InvoicePaySchedule.DueAmt,
                            0
                        ) > 0
                        " + dateFilter + @"
                    )
                    SELECT SchemaCurrency.C_Currency_ID,
                           SchemaCurrency.ISO_Code AS CurrencyISO,
                           SchemaCurrency.CurSymbol AS CurrencySymbol,
                           SchemaCurrency.StdPrecision,
                           ROUND(
                               COALESCE(
                                   SUM(ScheduledData.ScheduledAmount),
                                   0
                               ),
                               COALESCE(
                                   SchemaCurrency.StdPrecision,
                                   2
                               )
                           ) AS ScheduledAmount,
                           COUNT(ScheduledData.ScheduledAmount) AS ScheduleCount
                    FROM SchemaCurrency SchemaCurrency
                    LEFT OUTER JOIN ScheduledData ScheduledData
                        ON (ScheduledData.AD_Client_ID =
                            SchemaCurrency.AD_Client_ID
                            AND ScheduledData.C_Currency_ID =
                            SchemaCurrency.C_Currency_ID
                        )
                    GROUP BY SchemaCurrency.C_Currency_ID,
                             SchemaCurrency.ISO_Code,
                             SchemaCurrency.CurSymbol,
                             SchemaCurrency.StdPrecision";

                dr = DB.ExecuteReader(sql);

                decimal scheduledAmountThisWeek = 0M;
                int scheduleCount = 0;
                int cCurrencyId = 0;
                int precision = 2;
                string currencyISO = string.Empty;
                string currencySymbol = string.Empty;

                if (dr != null && dr.Read())
                {
                    scheduledAmountThisWeek =
                        Util.GetValueOfDecimal(dr["ScheduledAmount"]);

                    scheduleCount =
                        Util.GetValueOfInt(dr["ScheduleCount"]);

                    cCurrencyId =
                        Util.GetValueOfInt(dr["C_Currency_ID"]);

                    precision =
                        Util.GetValueOfInt(dr["StdPrecision"]);

                    currencyISO =
                        Util.GetValueOfString(dr["CurrencyISO"]);

                    currencySymbol =
                        Util.GetValueOfString(dr["CurrencySymbol"]);
                }

                if (precision < 0)
                {
                    precision = 2;
                }

                scheduledAmountThisWeek = decimal.Round(
                    scheduledAmountThisWeek,
                    precision,
                    MidpointRounding.AwayFromZero
                );

                bool hasData = scheduleCount > 0;

                return Json(new
                {
                    success = true,
                    error = "",

                    title = GetMsg(
                        ctx,
                        "VAS_029_MessageScheduled",
                        "Scheduled"
                    ),

                    badge = GetMsg(
                        ctx,
                        "VAS_029_MessageWhy",
                        "Why"
                    ),

                    badgeText = GetMsg(
                        ctx,
                        "VAS_029_MessageWhy",
                        "Why"
                    ),

                    description = GetMsg(
                        ctx,
                        "VAS_029_MessageScheduledForPaymentThisWeek",
                        "Scheduled for payment this week"
                    ),

                    /*
                     * Main widget value: one total amount only.
                     */
                    value = scheduledAmountThisWeek,
                    mainMetric = scheduledAmountThisWeek,

                    mainMetricText = scheduledAmountThisWeek.ToString(
                        "F" + precision,
                        CultureInfo.InvariantCulture
                    ),

                    scheduledAmountThisWeek = scheduledAmountThisWeek,
                    scheduleCount = scheduleCount,

                    /*
                     * Kept empty for backward compatibility.
                     * Payment-method amounts are no longer returned.
                     */
                    groups = new object[0],

                    cCurrencyId = cCurrencyId,
                    currencyISO = currencyISO,
                    currencySymbol = currencySymbol,
                    symbol = currencySymbol,
                    precision = precision,
                    stdPrecision = precision,

                    dateFrom = FormatDate(weekFrom),
                    dateTo = FormatDate(weekTo.AddDays(-1)),

                    hasData = hasData
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception)
            {
                string errorMessage = GetMsg(
                    ctx,
                    "VAS_029_MessageLoadError",
                    "Failed to load scheduled amount"
                );

                return Json(new
                {
                    success = false,
                    error = errorMessage,
                    errorText = errorMessage,
                    hasData = false
                }, JsonRequestBehavior.AllowGet);
            }
            finally
            {
                if (dr != null)
                {
                    dr.Close();
                    dr.Dispose();
                    dr = null;
                }
            }
        }

        private string GetDateFilter(
            string columnName,
            DateTime dateFrom,
            DateTime dateTo)
        {
            string dateFromText = FormatDate(dateFrom);
            string dateToText = FormatDate(dateTo);

            if (DB.IsOracle())
            {
                return @"
                    AND " + columnName + @" >= TO_DATE('" +
                    dateFromText + @"', 'YYYY-MM-DD')
                    AND " + columnName + @" < TO_DATE('" +
                    dateToText + @"', 'YYYY-MM-DD')
                ";
            }

            return @"
                AND " + columnName + @" >= DATE '" +
                dateFromText + @"'
                AND " + columnName + @" < DATE '" +
                dateToText + @"'
            ";
        }

        private string FormatDate(DateTime date)
        {
            return date.ToString(
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture
            );
        }

        private string GetMsg(
            Ctx ctx,
            string key,
            string fallback)
        {
            string msg = Msg.GetMsg(ctx, key);

            if (string.IsNullOrEmpty(msg)
                || msg == key
                || msg == "[" + key + "]")
            {
                return fallback;
            }

            return msg;
        }
    }
}