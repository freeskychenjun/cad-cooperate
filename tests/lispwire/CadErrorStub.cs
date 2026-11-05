// CadErrorStub.cs - test-only stand-in for the CadError exception that scripts\helper\LispTemplate.cs
// throws. The real CadError lives in CadHelper.cs, which drags in COM/System.Management and cannot be
// compiled offline; LispTemplate only needs the type + its four constructor fields. This stub is
// compiled ONLY by tests\selftest_lisp.ps1 (never into bin\cache\<sha>\CadHelper.exe, which compiles
// scripts\helper\*.cs and therefore the real CadError).

using System;

namespace CadHelper
{
    internal sealed class CadError : Exception
    {
        public string Code;
        public string Hint;
        public string Detail;

        public CadError(string code, string message, string hint, string detail)
            : base(message)
        {
            Code = code;
            Hint = hint;
            Detail = detail;
        }
    }
}
