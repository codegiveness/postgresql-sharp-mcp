// SPDX-FileCopyrightText: 2026 codegiveness
// SPDX-License-Identifier: MIT

namespace PostgreSqlMcp.Verify;

/// <summary>Constant result rows that the verifier scenarios compare against; shared static arrays instead of one allocation per assertion (CA1861).</summary>
internal static class ExpectedRows
{
    public static readonly string[][] AOnly = [["A_ONLY"]];
    public static readonly string[][] AOnlyThenWriteOk = [["A_ONLY"], ["WRITE_OK"]];
    public static readonly string[][] BOnly = [["B_ONLY"]];
    public static readonly string[][] BOnlyThenPackageWrite = [["B_ONLY"], ["PACKAGE_WRITE"]];
    public static readonly string[][] DynamicOnly = [["DYNAMIC_ONLY"]];
    public static readonly string[][] LockA = [["lock_a", "LOCK_A"]];
    public static readonly string[][] LockB = [["lock_b", "LOCK_B"]];
    public static readonly string[][] QuotedValues = [["a;b", "escaped';still-string", "ordinary;string"]];
    public static readonly string[][] SensitiveMarker = [["sensitive-result-marker"]];
    public static readonly string[][] TenantA = [["tenant_a"]];
    public static readonly string[][] TenantB = [["tenant_b"]];
    public static readonly string[][] TenantBWithBOnly = [["tenant_b", "B_ONLY"]];
    public static readonly string[][] WriteTestTable = [["mcp_write_test"]];
    public static readonly string[][] JsonCastValue = [["v"]];
    public static readonly int[][] One = [[1]];
    public static readonly int[][] Ten = [[10]];
    public static readonly int[][] Zero = [[0]];
}
