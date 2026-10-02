# Best Practices: exact questions and copy-ready answers

Use this with the [Passing questionnaire](https://www.bestpractices.dev/en/projects/15155/passing/edit). No JSON editing is necessary.

The quoted sentences below come from [this project’s public Passing questionnaire](https://www.bestpractices.dev/en/projects/15155/passing), not from internal field names. Open the named section, find the quoted question, choose the stated answer, and paste the explanation into its justification box when one is provided. Some answers may already be selected. Review the evidence before saving.

These are 34 reviewed proposals: 32 **Met** and two **N/A**. This is not a claim that every Passing requirement is satisfied or that any answer has been saved on the website. Policy references use the published default branch; use them once this revision is on main.

## Basics

### 1. Basic project website content

> The project website MUST succinctly describe what the software does (what problem does it solve?).

**Choose: Met**

**Paste as the explanation:**

README describes the PostgreSQL stdio MCP server, nine tools, explicit database targeting and read-only defaults: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/README.md

### 2. Basic project website content

> The project website MUST provide information on how to: obtain, provide feedback (as bug reports or enhancements), and contribute to the software.

**Choose: Met**

**Paste as the explanation:**

README documents installation and links CONTRIBUTING for bug reports, enhancement requests and pull requests: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md

### 3. Basic project website content

> The information on how to contribute SHOULD include the requirements for acceptable contributions (e.g., a reference to any required coding standard).

**Choose: Met**

**Paste as the explanation:**

Contribution guidance specifies dependency direction, supported tooling, meaningful regression evidence, confidentiality and review requirements: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md

### 4. Documentation

> The project MUST provide reference documentation that describes the external interface (both input and output) of the software produced by the project.

**Choose: Met**

**Paste as the explanation:**

README documents CLI/configuration inputs, all nine MCP tools, structured rowset outputs, pagination, clipping and error envelopes: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/README.md#tools

### 5. Other

> The project SHOULD provide documentation in English and be able to accept bug reports and comments about code in English.

**Choose: Met**

**Paste as the explanation:**

README and contribution/reporting instructions are in English: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md

### 6. Other

> The project MUST be maintained.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: the non-archived repository has substantive recent maintenance, including runtime-resource/security verification fixes in e0572221bb3f3a6c4a8bce2791435f558fbf20db and real-build/secret/container security gates in bde167e95e0fb3ff74b895db55c87f4b04d69b0c, with successful integration CI: https://github.com/codegiveness/postgresql-sharp-mcp/commit/e0572221bb3f3a6c4a8bce2791435f558fbf20db and https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816424 . These demonstrate meaningful maintenance, not a measured report-response history.

## Change Control

### 7. Public version-controlled source repository

> To enable collaborative review, the project's source repository MUST include interim versions for review between releases; it MUST NOT include only final releases.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: released v0.2.0 identifies 1595dc1683c709e4265027048567c06216751afa; subsequent public commits a9f4ec3fdac162e0ce7a1e5e7dbb5d5e13cb3b38, e0572221bb3f3a6c4a8bce2791435f558fbf20db and bde167e95e0fb3ff74b895db55c87f4b04d69b0c expose interim maintenance between releases: https://github.com/codegiveness/postgresql-sharp-mcp/compare/v0.2.0...bde167e95e0fb3ff74b895db55c87f4b04d69b0c

### 8. Unique version numbering

> The project results MUST have a unique version identifier for each release intended to be used by users.

**Choose: Met**

**Paste as the explanation:**

Public releases use unique version tags v0.1.0 and v0.2.0: https://github.com/codegiveness/postgresql-sharp-mcp/releases

### 9. Unique version numbering

> It is SUGGESTED that the Semantic Versioning (SemVer) or Calendar Versioning (CalVer) version numbering format be used for releases. It is SUGGESTED that those who use CalVer include a micro level value.

**Choose: Met**

**Paste as the explanation:**

Release identifiers use major.minor.patch numbering: https://github.com/codegiveness/postgresql-sharp-mcp/releases

### 10. Unique version numbering

> It is SUGGESTED that projects identify each release within their version control system. For example, it is SUGGESTED that those using git identify each release using git tags.

**Choose: Met**

**Paste as the explanation:**

Public releases identify their corresponding Git tags: https://github.com/codegiveness/postgresql-sharp-mcp/releases

## Reporting

### 11. Bug-reporting process

> The project SHOULD use an issue tracker for tracking individual issues.

**Choose: Met**

**Paste as the explanation:**

Contribution instructions direct actionable bug reports and enhancements to GitHub issues: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md#issues-pull-requests-and-reviews

### 12. Bug-reporting process

> The project MUST have a publicly available archive for reports and responses for later searching.

**Choose: Met**

**Paste as the explanation:**

GitHub issues and pull requests provide searchable, URL-addressable public reports and responses: https://github.com/codegiveness/postgresql-sharp-mcp/issues

### 13. Vulnerability report process

> The project MUST publish the process for reporting vulnerabilities on the project site.

**Choose: Met**

**Paste as the explanation:**

SECURITY documents private reporting, sanitized reproduction requirements and fallback contact requests: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/SECURITY.md#reporting-a-vulnerability

### 14. Vulnerability report process

> If private vulnerability reports are supported, the project MUST include how to send the information in a way that is kept private.

**Choose: Met**

**Paste as the explanation:**

GitHub private vulnerability reporting is enabled (repository API verified 2026-10-02); SECURITY links the HTTPS reporting route: https://github.com/codegiveness/postgresql-sharp-mcp/security

## Quality

### 15. Working build system

> If the software produced by the project requires building for use, the project MUST provide a working build system that can automatically rebuild the software from source code.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: the .NET build is documented in CONTRIBUTING, and CI run 36975816424 on bde167e95e0fb3ff74b895db55c87f4b04d69b0c successfully restored locked dependencies, built and exercised PostgreSQL integration, and built packages: https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816424 . This does not verify the unmerged candidate.

### 16. Working build system

> It is SUGGESTED that common tools be used for building the software.

**Choose: Met**

**Paste as the explanation:**

The source build uses the .NET SDK and MSBuild: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md#build-from-source

### 17. Working build system

> The project SHOULD be buildable using only FLOSS tools.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: the documented source build uses dotnet build without requiring a proprietary IDE or hosted service: https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/CONTRIBUTING.md#build-from-source . The .NET SDK and MSBuild are MIT-licensed FLOSS: https://github.com/dotnet/sdk/blob/main/LICENSE.TXT and https://github.com/dotnet/msbuild/blob/main/LICENSE . Docker and npm are needed for separate verification/package paths, not the source executable build.

### 18. Automated test suite

> The project MUST use at least one automated test suite that is publicly released as FLOSS (this test suite may be maintained as a separate FLOSS project). The project MUST clearly show or document how to run the test suite(s) (e.g., via a continuous integration (CI) script or via documentation in files such as BUILD.md, README.md, or CONTRIBUTING.md).

**Choose: Met**

**Paste as the explanation:**

The public MIT-licensed .NET verifier exercises actual MCP calls against a disposable PostgreSQL fixture; CONTRIBUTING documents invocation: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md#verify-behavior

### 19. Automated test suite

> A test suite SHOULD be invocable in a standard way for that language.

**Choose: Met**

**Paste as the explanation:**

The suite is invoked using dotnet run --project tools/PostgreSqlMcp.Verify -c Release -- integration: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md#verify-behavior

### 20. Automated test suite

> It is SUGGESTED that the project implement continuous integration (where new or changed code is frequently integrated into a central code repository and automated tests are run on the result).

**Choose: Met**

**Paste as the explanation:**

CI runs integration and native package installation on pushes and pull requests; run 36975816424 succeeded on revision bde167e95e0fb3ff74b895db55c87f4b04d69b0c: https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816424

### 21. New functionality testing

> The project MUST have a general policy (formal or not) that as major new functionality is added to the software produced by the project, tests of that functionality should be added to an automated test suite.

**Choose: Met**

**Paste as the explanation:**

The CONTRIBUTING.md Verify behavior section explicitly requires automated consumer-visible behavior coverage for major new functionality, with meaningful boundaries, transitions and errors: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md#verify-behavior

### 22. New functionality testing

> The project MUST have evidence that the test_policy for adding tests has been adhered to in the most recent major changes to the software produced by the project.

**Choose: Met**

**Paste as the explanation:**

Released-source evidence: v0.2.0 replaced the npm launcher and delivery tooling with .NET, as summarized in https://github.com/codegiveness/postgresql-sharp-mcp/blob/v0.2.0/CHANGELOG.md . The same release commit added the maintained verifier, including offline installation, native entrypoints, missing-.NET prerequisite failure and real MCP initialization/tool calls/shutdown checks: https://github.com/codegiveness/postgresql-sharp-mcp/commit/1595dc1683c709e4265027048567c06216751afa and https://github.com/codegiveness/postgresql-sharp-mcp/blob/v0.2.0/tools/PostgreSqlMcp.Verify/Packages.cs . Successful later CI run 36975816424 exercised these distributions on Linux, macOS and Windows; this is evidence of functionality testing, not branch-coverage measurement.

### 23. New functionality testing

> It is SUGGESTED that this policy on adding tests (see test_policy) be documented in the instructions for change proposals.

**Choose: Met**

**Paste as the explanation:**

The instructions for contributions document the policy of adding automated tests for major new functionality and explain acceptable regression coverage: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/CONTRIBUTING.md#verify-behavior

### 24. Warning flags

> The project MUST enable one or more compiler warning flags, a "safe" language mode, or use a separate "linter" tool to look for code quality errors or common simple mistakes, if there is at least one FLOSS tool that can implement this criterion in the selected language.

**Choose: Met**

**Paste as the explanation:**

Shared build settings enable nullable checking and TreatWarningsAsErrors: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/Directory.Build.props

### 25. Warning flags

> The project MUST address warnings.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: CI run 36975816424 passed its real integration/build and package jobs on bde167e95e0fb3ff74b895db55c87f4b04d69b0c with shared TreatWarningsAsErrors enabled: https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816424 and https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/Directory.Build.props . This is not evidence about an unmerged candidate.

### 26. Warning flags

> It is SUGGESTED that projects be maximally strict with warnings in the software produced by the project, where practical.

**Choose: Met**

**Paste as the explanation:**

All projects inherit nullable checking and TreatWarningsAsErrors: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/Directory.Build.props

## Security

### 27. Use basic good cryptographic practices

> The software produced by the project MUST use, by default, only cryptographic protocols and algorithms that are publicly published and reviewed by experts (if cryptographic protocols and algorithms are used).

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: database connections delegate to Npgsql 10.0.3, whose authentication uses published PostgreSQL protocols (including SCRAM-SHA-256) and whose TLS implementation delegates to .NET SslStream: https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/src/PostgreSqlMcp.Core/DatabaseRegistry.cs and https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.Auth.cs and https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.cs . No project-specific cryptographic protocol is implemented. Published protocols do not establish safe negotiated algorithms, key lengths, certificate validation or forward secrecy; those stronger criteria remain unresolved.

### 28. Use basic good cryptographic practices

> If the software produced by the project is an application or library, and its primary purpose is not to implement cryptography, then it SHOULD only call on software specifically designed to implement cryptographic functions; it SHOULD NOT re-implement its own.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: the application delegates PostgreSQL authentication and TLS to Npgsql rather than implementing cryptographic primitives. Npgsql delegates TLS to .NET SslStream and SCRAM primitives to System.Security.Cryptography (SHA256, HMACSHA256, PBKDF2 and RandomNumberGenerator): https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/src/PostgreSqlMcp.Core/DatabaseRegistry.cs and https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.Auth.cs and https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.cs

### 29. Use basic good cryptographic practices

> All functionality in the software produced by the project that depends on cryptography MUST be implementable using FLOSS.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: the cryptographic functionality can run with FLOSS Npgsql and .NET on Linux, using their open authentication/TLS implementations; no proprietary cryptographic service is required. The repository distributes their permissive Npgsql and MIT .NET licenses: https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/LICENSES/Npgsql.txt and https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/LICENSES/DotNet.txt . Implementation: https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/Internal/NpgsqlConnector.Auth.cs

### 30. Use basic good cryptographic practices

> If the software produced by the project causes the storing of passwords for authentication of external users, the passwords MUST be stored as iterated hashes with a per-user salt by using a key stretching (iterated) algorithm (e.g., Argon2id, Bcrypt, Scrypt, or PBKDF2). See also OWASP Password Storage Cheat Sheet.

**Choose: N/A**

**Paste as the explanation:**

The passing criterion explicitly excludes outbound authentication credentials. This stdio MCP server does not authenticate external MCP users or store their password verifiers; configured PostgreSQL credentials authenticate outbound database connections. Public trust-boundary and credential documentation: https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/SECURITY.md#trust-boundary and https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/src/PostgreSqlMcp.Core/ServerOptions.cs . This exemption does not waive protecting outbound secrets or database TLS.

## Analysis

### 31. Static code analysis

> It is SUGGESTED that at least one of the static analysis tools used for the static_analysis criterion include rules or approaches to look for common vulnerabilities in the analyzed language or environment.

**Choose: Met**

**Paste as the explanation:**

CodeQL runs security-extended queries for C# and GitHub Actions; run 36975816274 succeeded on revision bde167e95e0fb3ff74b895db55c87f4b04d69b0c: https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816274

### 32. Static code analysis

> It is SUGGESTED that static source code analysis occur on every commit or at least daily.

**Choose: Met**

**Paste as the explanation:**

CodeQL is configured on main pushes and pull requests, with a weekly schedule: https://github.com/codegiveness/postgresql-sharp-mcp/blob/main/.github/workflows/codeql.yml

### 33. Dynamic code analysis

> It is SUGGESTED that if the software produced by the project includes software written using a memory-unsafe language (e.g., C or C++), then at least one dynamic tool (e.g., a fuzzer or web application scanner) be routinely used in combination with a mechanism to detect memory safety problems such as buffer overwrites. If the project does not produce software written in a memory-unsafe language, choose "not applicable" (N/A).

**Choose: N/A**

**Paste as the explanation:**

The passing criterion explicitly permits N/A when the project does not produce software written in a memory-unsafe language. Maintained first-party application and tooling sources are C# without unsafe blocks or AllowUnsafeBlocks; the public source tree contains no first-party C/C++ implementation: https://github.com/codegiveness/postgresql-sharp-mcp/tree/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/src and https://github.com/codegiveness/postgresql-sharp-mcp/tree/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/tools . Native runtime/dependency code is not a claim of memory-safety certification.

### 34. Dynamic code analysis

> It is SUGGESTED that the project use a configuration for at least some dynamic analysis (such as testing or fuzzing) which enables many assertions. In many cases these assertions should not be enabled in production builds.

**Choose: Met**

**Paste as the explanation:**

Public-source evidence: the verification configuration runs deterministic varied-input SQL checks with always-enabled Check.Equal/Check.That and expected-error assertions, including exact PostgreSQL literal round trips, no unexpected truncation and second-statement/control-statement rejection: https://github.com/codegiveness/postgresql-sharp-mcp/blob/bde167e95e0fb3ff74b895db55c87f4b04d69b0c/tools/PostgreSqlMcp.Verify/FuzzChecks.cs . These assertions belong to the test verifier, not the production server, and integration succeeded in https://github.com/codegiveness/postgresql-sharp-mcp/actions/runs/36975816424 . This does not establish release-specific pre-release analysis or any branch-coverage percentage.

## Questions that still need owner knowledge or additional evidence

Do not select Met merely because CI is green. In particular, this review does not establish the developer-knowledge questions, historical bug/enhancement/vulnerability response times, measured branch coverage, release-specific pre-release static/dynamic analysis, universal cryptographic defaults/key lengths/forward secrecy, absence of valid leaked credentials, or vulnerability-remediation histories. Read those questions separately and answer from evidence; do not use N/A as a substitute for missing information.

## Save

Sign in as the project owner and save the questionnaire. Repository changes cannot save the website’s answers. The README badge displays the website’s saved state and may be cached; it is not a security certification.

Quoted question text is attributed to the OpenSSF Best Practices badge contributors and reproduced under [CC-BY-3.0 or later](https://creativecommons.org/licenses/by/3.0/). Repository-specific explanations are this project's review.
