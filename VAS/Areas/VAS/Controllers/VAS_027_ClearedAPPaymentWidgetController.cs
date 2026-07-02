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
    /*
     * Labels / Message Keys
     * 1 | Cleared                                | VAS_027_messageCleared
     * 2 | Why                                    | VAS_027_messageWhy
     * 3 | Of last month's AP payments reconciled | VAS_027_messageAPPaymentClearedWhy
     * 4 | Failed to load cleared AP payments     | VAS_027_messageLoadError
     */
    public class VAS_027_ClearedAPPaymentWidgetController : Controller
    {
        [AjaxAuthorizeAttribute]
        [AjaxSessionFilterAttribute]
        public JsonResult GetClearedAPPayment()
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
            IDataReader dr = null;

            try
            {
                DateTime today = DateTime.Today;

                /*
                 * Previous calendar month:
                 *
                 * dateFrom = first day of previous month
                 * dateTo   = first day of current month
                 *
                 * Range:
                 * DateTrx >= dateFrom
                 * DateTrx < dateTo
                 */
                DateTime dateFrom = new DateTime(
                    today.Year,
                    today.Month,
                    1
                ).AddMonths(-1);

                DateTime dateTo = new DateTime(
                    today.Year,
                    today.Month,
                    1
                );

                string dateFilter = GetDateFilter(
                    "p.DateTrx",
                    dateFrom,
                    dateTo
                );

                string sql = @"
                    SELECT COUNT(1) AS TotalPayments,
                           COALESCE(
                               SUM(
                                   CASE
                                       WHEN COALESCE(p.IsReconciled, 'N') = 'Y' THEN 1
                                       ELSE 0
                                   END
                               ),
                               0
                           ) AS ClearedPayments
                    FROM C_Payment p
                    WHERE p.IsActive = 'Y'
                    AND p.IsReceipt = 'N'
                    AND p.DocStatus IN ('CO', 'CL')
                " + dateFilter;

                /*
                 * Apply security access only to the physical main table.
                 */
                sql = MRole.GetDefault(ctx).AddAccessSQL(
                    sql,
                    "p",
                    MRole.SQL_FULLYQUALIFIED,
                    MRole.SQL_RO
                );

                int totalPayments = 0;
                int clearedPayments = 0;
                decimal clearedPercentage = 0M;

                dr = DB.ExecuteReader(sql);

                if (dr != null && dr.Read())
                {
                    totalPayments = Util.GetValueOfInt(
                        dr["TotalPayments"]
                    );

                    clearedPayments = Util.GetValueOfInt(
                        dr["ClearedPayments"]
                    );
                }

                if (totalPayments > 0)
                {
                    clearedPercentage = decimal.Round(
                        clearedPayments * 100M / totalPayments,
                        2,
                        MidpointRounding.AwayFromZero
                    );
                }

                return Json(new
                {
                    success = true,
                    error = "",

                    title = GetMsg(
                        ctx,
                        "VAS_027_messageCleared",
                        "Cleared"
                    ),

                    badge = GetMsg(
                        ctx,
                        "VAS_027_messageWhy",
                        "Why"
                    ),

                    badgeText = GetMsg(
                        ctx,
                        "VAS_027_messageWhy",
                        "Why"
                    ),

                    description = GetMsg(
                        ctx,
                        "VAS_027_messageAPPaymentClearedWhy",
                        "Of last month's AP payments reconciled"
                    ),

                    value = clearedPercentage,
                    mainMetric = clearedPercentage,

                    mainMetricText = clearedPercentage.ToString(
                        "0.00",
                        CultureInfo.InvariantCulture
                    ),

                    clearedPercentage = clearedPercentage,
                    totalPayments = totalPayments,
                    clearedPayments = clearedPayments,

                    precision = 2,
                    stdPrecision = 2,

                    dateFrom = FormatDate(dateFrom),
                    dateTo = FormatDate(dateTo.AddDays(-1)),

                    hasData = totalPayments > 0
                }, JsonRequestBehavior.AllowGet);
            }
            catch (Exception)
            {
                string errorMessage = GetMsg(
                    ctx,
                    "VAS_027_messageLoadError",
                    "Failed to load cleared AP payments"
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

        /// <summary>
        /// Creates an ANSI-standard half-open date filter.
        ///
        /// DATE 'YYYY-MM-DD' is supported by:
        /// - Oracle
        /// - PostgreSQL
        /// </summary>
        private string GetDateFilter(
            string columnName,
            DateTime dateFrom,
            DateTime dateTo)
        {
            string dateFromText = FormatDate(dateFrom);
            string dateToText = FormatDate(dateTo);

            return @"
                AND " + columnName + @" >= DATE '" + dateFromText + @"'
                AND " + columnName + @" < DATE '" + dateToText + @"'
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