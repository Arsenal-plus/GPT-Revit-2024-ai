// -----------------------------------------------------------------------------
// Pins the ledger columns that horizun_cde_cloud issue_create maps a finding from
// (CdeCloudIssues.cs: LedgerTitle, FindingTextKeys, FindingIdKeys, FindingContextKeys).
// The Server test CdeCloudIssuesTests.A_ledger_csv_row_and_json_row_make_an_issue feeds
// a row with these exact names; renaming one here without the mapper would make a real
// ledger row lose its title or its clash context again, so the rename has to fail here.
// -----------------------------------------------------------------------------
using System.Linq;
using Horizun.Revit.Core;
using Xunit;

namespace Horizun.Core.Tests
{
    public class CoordinationLedgerColumnsTests
    {
        [Fact]
        public void The_columns_an_acc_issue_is_mapped_from_are_still_in_the_ledger_header()
        {
            string[] mapped =
            {
                "finding_id", "note", "category_a", "category_b", "side_a", "side_b", "point_mm",
                "priority", "responsible", "immovable_discipline", "scope"
            };
            foreach (string column in mapped)
                Assert.Contains(column, CoordinationRules.CsvHeader);
            Assert.DoesNotContain("title", CoordinationRules.CsvHeader.ToList());
        }
    }
}
