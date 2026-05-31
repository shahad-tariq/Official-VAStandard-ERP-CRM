using Newtonsoft.Json;
using System.Data;
using System.Web.Mvc;
using VAdvantage.Classes;
using VAdvantage.DataBase;
using VAdvantage.Model;
using VAdvantage.Utility;
using VIS.Filters;

namespace VIS.Controllers
{
    public class AvgDaysToPayController : Controller
    {
        /// <summary>
        /// Returns the weighted-average days-to-pay for the current quarter,
        /// the same figure for the previous quarter, the day difference,
        /// and a display label such as "3 days faster than last quarter".
        /// </summary>
        [AjaxAuthorizeAttribute]
        [AjaxSessionFilterAttribute]
        public JsonResult GetAvgDaysToPay()
        {
            if (Session["ctx"] == null)
            {
                return Json(new { error = Msg.GetMsg(Env.GetCtx(), "SessionExpired") ?? "Session Expired" }, JsonRequestBehavior.AllowGet);
            }

            Ctx ctx = Session["ctx"] as Ctx;

            /*
             * PostgreSQL version:
             * Do not use SYSDATE because PostgreSQL treats it as a column name.
             * CURRENT_DATE is the PostgreSQL equivalent for today's date without time.
             */
            string currentPeriodDateCondition =
                " CURRENT_DATE BETWEEN CAST(p.StartDate AS DATE) AND CAST(p.EndDate AS DATE) ";

            /*
             * PostgreSQL:
             * DATE - DATE returns number of days.
             * GREATEST keeps negative values at zero.
             */
            string daysToPayCondition =
                " GREATEST(CAST((CAST(pay.DateAcct AS DATE) - CAST(ips.DueDate AS DATE)) AS NUMERIC), 0) ";

            string currentPeriodSql = @"
                SELECT CAST(TO_CHAR(p.StartDate, 'Q') AS NUMERIC) AS CurrentQuarter,
                       CAST(TO_CHAR(p.StartDate, 'YYYY') AS NUMERIC) AS CurrentYear
                FROM AD_ClientInfo ci
                INNER JOIN C_Calendar cal ON (ci.C_Calendar_ID = cal.C_Calendar_ID)
                INNER JOIN C_Year yr ON (cal.C_Calendar_ID = yr.C_Calendar_ID)
                INNER JOIN C_Period p ON (yr.C_Year_ID = p.C_Year_ID)
                WHERE ci.AD_Client_ID = " + ctx.GetAD_Client_ID() + @"
                AND " + currentPeriodDateCondition + @"
                FETCH FIRST 1 ROW ONLY";

            string paymentsSql = @"
                SELECT CAST(TO_CHAR(pay.DateAcct, 'Q') AS NUMERIC) AS PayQuarter,
                       CAST(TO_CHAR(pay.DateAcct, 'YYYY') AS NUMERIC) AS PayYear,
                       " + daysToPayCondition + @" AS Days_To_Pay,
                       COALESCE(al.Amount, 0) AS Amount
                FROM C_Invoice i
                INNER JOIN C_InvoicePaySchedule ips ON (ips.C_Invoice_ID = i.C_Invoice_ID)
                INNER JOIN C_AllocationLine al ON (al.C_InvoicePaySchedule_ID = ips.C_InvoicePaySchedule_ID)
                INNER JOIN C_Payment pay ON (pay.C_Payment_ID = al.C_Payment_ID)
                WHERE i.IsSoTrx = 'Y'
                AND i.DocStatus IN ('CO', 'CL')
                AND al.C_Payment_ID IS NOT NULL
                AND i.IsActive = 'Y'
                AND ips.IsActive = 'Y'
                AND al.IsActive = 'Y'
                AND pay.IsActive = 'Y'";

            /*
             * Apply access SQL only on the main physical table C_Invoice alias i.
             */
            paymentsSql = MRole.GetDefault(ctx).AddAccessSQL(
                paymentsSql,
                "i",
                MRole.SQL_FULLYQUALIFIED,
                MRole.SQL_RO
            );

            string sql = @"
                WITH CurrentPeriod AS (
                    " + currentPeriodSql + @"
                ),
                Payments AS (
                    " + paymentsSql + @"
                ),
                ClassifiedPayments AS (
                    SELECT p.Days_To_Pay,
                           p.Amount,
                           CASE
                               WHEN p.PayQuarter = cp.CurrentQuarter
                                AND p.PayYear = cp.CurrentYear
                                   THEN 'Current'

                               WHEN p.PayQuarter = (
                                        CASE
                                            WHEN cp.CurrentQuarter = 1 THEN 4
                                            ELSE cp.CurrentQuarter - 1
                                        END
                                    )
                                AND (
                                        (cp.CurrentQuarter > 1 AND p.PayYear = cp.CurrentYear)
                                        OR
                                        (cp.CurrentQuarter = 1 AND p.PayYear = cp.CurrentYear - 1)
                                    )
                                   THEN 'Previous'
                           END AS QuarterFlag
                    FROM Payments p
                    CROSS JOIN CurrentPeriod cp
                )
                SELECT ROUND(
                           COALESCE(
                               SUM(CASE WHEN QuarterFlag = 'Current' THEN Days_To_Pay * Amount ELSE 0 END)
                               / NULLIF(SUM(CASE WHEN QuarterFlag = 'Current' THEN Amount ELSE 0 END), 0),
                               0
                           ),
                           0
                       ) AS Current_Quarter_Avg_Days,

                       ROUND(
                           COALESCE(
                               SUM(CASE WHEN QuarterFlag = 'Previous' THEN Days_To_Pay * Amount ELSE 0 END)
                               / NULLIF(SUM(CASE WHEN QuarterFlag = 'Previous' THEN Amount ELSE 0 END), 0),
                               0
                           ),
                           0
                       ) AS Previous_Quarter_Avg_Days
                FROM ClassifiedPayments
                WHERE QuarterFlag IS NOT NULL";

            int currentAvg = 0;
            int previousAvg = 0;
            int diffDays = 0;
            string displayText = Msg.GetMsg(ctx, "NoChange") ?? "No change";

            IDataReader dr = null;

            try
            {
                dr = DB.ExecuteReader(sql);

                if (dr != null && dr.Read())
                {
                    currentAvg = Util.GetValueOfInt(dr["Current_Quarter_Avg_Days"]);
                    previousAvg = Util.GetValueOfInt(dr["Previous_Quarter_Avg_Days"]);

                    diffDays = currentAvg - previousAvg;

                    if (diffDays < 0)
                    {
                        displayText =
                            System.Math.Abs(diffDays).ToString()
                            + (Msg.GetMsg(ctx, "VAS_DaysFasterThanLastQuarter") ?? " days faster than last quarter");
                    }
                    else if (diffDays > 0)
                    {
                        displayText =
                            diffDays.ToString()
                            + (Msg.GetMsg(ctx, "VAS_DaysSlowerThanLastQuarter") ?? " days slower than last quarter");
                    }
                    else
                    {
                        displayText = Msg.GetMsg(ctx, "VIS_NoChange") ?? "No change";
                    }
                }
            }
            finally
            {
                if (dr != null)
                {
                    dr.Close();
                }
            }

            var result = new
            {
                currentAvgDays = currentAvg,
                previousAvgDays = previousAvg,
                differenceDays = diffDays,
                displayText = displayText
            };

            return Json(JsonConvert.SerializeObject(result), JsonRequestBehavior.AllowGet);
        }
    }
}