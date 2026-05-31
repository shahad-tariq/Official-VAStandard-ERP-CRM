using Newtonsoft.Json;
using System.Collections.Generic;
using System.Data;
using System.Web.Mvc;
using VAdvantage.Classes;
using VAdvantage.DataBase;
using VAdvantage.Model;
using VAdvantage.Utility;
using VIS.Filters;

namespace VIS.Controllers
{
    public class TopDebtorsController : Controller
    {
        /// <summary>
        /// Returns the top 5 customers with the largest outstanding unpaid invoice balances
        /// (VA009_IsPaid = 'N', DocStatus IN ('CO','CL'), IsSoTrx = 'Y'), converted to the
        /// client's accounting base schema currency.
        /// Credit notes are subtracted.
        /// Includes the max days overdue, status text, and base-currency symbol.
        /// </summary>
        [AjaxAuthorizeAttribute]
        [AjaxSessionFilterAttribute]
        public JsonResult GetTopDebtors()
        {
            if (Session["ctx"] == null)
            {
                return Json(new { error = Msg.GetMsg(Env.GetCtx(), "SessionExpired") ?? "Session Expired" }, JsonRequestBehavior.AllowGet);
            }

            Ctx ctx = Session["ctx"] as Ctx;

            string schemaCurrencySql = @"
                SELECT ci.AD_Client_ID,
                       acc.C_Currency_ID AS Acct_Currency_ID,
                       cur.StdPrecision,
                       CASE 
                           WHEN cur.CurSymbol IS NOT NULL THEN cur.CurSymbol 
                           ELSE cur.ISO_Code 
                       END AS Cur_Symbol
                FROM AD_ClientInfo ci
                INNER JOIN C_AcctSchema acc ON (ci.C_AcctSchema1_ID = acc.C_AcctSchema_ID)
                INNER JOIN C_Currency cur ON (acc.C_Currency_ID = cur.C_Currency_ID)";

            /*
             * PostgreSQL fix:
             * Do not use SYSDATE.
             * CURRENT_DATE is PostgreSQL's current date.
             * DATE - DATE returns number of days.
             */
            string daysOverdueExpr =
                "CAST((CURRENT_DATE - CAST(ips.DueDate AS DATE)) AS NUMERIC)";

            string customerOutstandingSql = @"
                SELECT bp.C_BPartner_ID AS Customer_ID,
                       bp.Name AS Customer_Name,
                       SUM(
                           CASE
                               WHEN i.IsReturnTrx = 'N' THEN
                                   CASE
                                       WHEN i.C_Currency_ID = sc.Acct_Currency_ID THEN COALESCE(ips.DueAmt, 0)
                                       ELSE CurrencyConvert(
                                           COALESCE(ips.DueAmt, 0),
                                           i.C_Currency_ID,
                                           sc.Acct_Currency_ID,
                                           i.DateAcct,
                                           i.C_ConversionType_ID,
                                           i.AD_Client_ID,
                                           i.AD_Org_ID
                                       )
                                   END

                               WHEN i.IsReturnTrx = 'Y' THEN
                                   -CASE
                                       WHEN i.C_Currency_ID = sc.Acct_Currency_ID THEN COALESCE(ips.DueAmt, 0)
                                       ELSE CurrencyConvert(
                                           COALESCE(ips.DueAmt, 0),
                                           i.C_Currency_ID,
                                           sc.Acct_Currency_ID,
                                           i.DateAcct,
                                           i.C_ConversionType_ID,
                                           i.AD_Client_ID,
                                           i.AD_Org_ID
                                       )
                                   END

                               ELSE 0
                           END
                       ) AS Total_Outstanding_Amount,

                       MAX(
                           CASE
                               WHEN " + daysOverdueExpr + @" > 0 THEN " + daysOverdueExpr + @"
                               ELSE 0
                           END
                       ) AS Max_Days_Overdue

                FROM C_InvoicePaySchedule ips
                INNER JOIN C_Invoice i ON (ips.C_Invoice_ID = i.C_Invoice_ID)
                INNER JOIN C_BPartner bp ON (i.C_BPartner_ID = bp.C_BPartner_ID)
                INNER JOIN schema_currency sc ON (sc.AD_Client_ID = i.AD_Client_ID)
                WHERE i.IsSoTrx = 'Y'
                AND ips.VA009_IsPaid = 'N'
                AND i.DocStatus IN ('CO', 'CL')";

            /*
             * Apply MRole only on main physical table C_Invoice alias i.
             */
            customerOutstandingSql = MRole.GetDefault(ctx).AddAccessSQL(
                customerOutstandingSql,
                "i",
                MRole.SQL_FULLYQUALIFIED,
                MRole.SQL_RO
            );

            customerOutstandingSql += @"
                GROUP BY bp.C_BPartner_ID,
                         bp.Name";

            string precisionDataSql = @"
                SELECT MAX(StdPrecision) AS Prec,
                       MAX(Cur_Symbol) AS Cur_Symbol
                FROM schema_currency";

            string sql = @"
                WITH schema_currency AS (
                    " + schemaCurrencySql + @"
                ),
                customer_outstanding AS (
                    " + customerOutstandingSql + @"
                ),
                precision_data AS (
                    " + precisionDataSql + @"
                )
                SELECT co.Customer_ID,
                       co.Customer_Name,
                       ROUND(co.Total_Outstanding_Amount, p.Prec) AS Total_Outstanding_Amount,
                       co.Max_Days_Overdue,
                       p.Cur_Symbol AS Currency_Symbol
                FROM customer_outstanding co
                CROSS JOIN precision_data p
                ORDER BY co.Total_Outstanding_Amount DESC
                FETCH FIRST 5 ROWS ONLY";

            string currencySymbol = "";
            var rows = new List<object>();

            IDataReader dr = null;

            try
            {
                dr = DB.ExecuteReader(sql);

                while (dr != null && dr.Read())
                {
                    if (string.IsNullOrEmpty(currencySymbol))
                    {
                        currencySymbol = Util.GetValueOfString(dr["Currency_Symbol"]);
                    }

                    int maxDaysOverdue = Util.GetValueOfInt(dr["Max_Days_Overdue"]);

                    rows.Add(new
                    {
                        customerName = dr["Customer_Name"]?.ToString(),
                        unpaidBalance = Util.GetValueOfDecimal(dr["Total_Outstanding_Amount"]),
                        daysOverdue = maxDaysOverdue,
                        statusText = maxDaysOverdue <= 0
                            ? (Msg.GetMsg(ctx, "VIS_NotYetDue") ?? "Not yet due")
                            : maxDaysOverdue.ToString() + (Msg.GetMsg(ctx, "VIS_DaysOverdue") ?? " days overdue")
                    });
                }
            }
            finally
            {
                if (dr != null)
                {
                    dr.Close();
                }
            }

            return Json(
                JsonConvert.SerializeObject(new
                {
                    symbol = currencySymbol,
                    rows = rows
                }),
                JsonRequestBehavior.AllowGet
            );
        }
    }
}