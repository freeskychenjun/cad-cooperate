// ZwcErrorStub.cs - test-only stand-in for the ZwcError exception that scripts\helper\LispTemplate.cs
// throws. The real ZwcError lives in ZwcHelper.cs, which drags in COM/System.Management and cannot be
// compiled offline; LispTemplate only needs the type + its four constructor fields. This stub is
// compiled ONLY by tests\selftest_lisp.ps1 (never into bin\cache\<sha>\ZwcHelper.exe, which compiles
// scripts\helper\*.cs and therefore the real ZwcError).

using System;

namespace ZwcHelper
{
    internal sealed class ZwcError : Exception
    {
        public string Code;
        public string Hint;
        public string Detail;

        public ZwcError(string code, string message, string hint, string detail)
            : base(message)
        {
            Code = code;
            Hint = hint;
            Detail = detail;
        }
    }
}
