using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using TiaOpenness.Core.Inspection;
using TiaOpenness.Contracts.Models;
using TiaOpenness.Contracts.Models.Errors;
using TiaOpenness.Core.Abstractions;

namespace TiaOpenness.Core.Mock
{

    /// <summary>Hands out <see cref="MockTiaSession"/> instances.</summary>
    public sealed class MockTiaSessionFactory : ITiaSessionFactory
    {
        public SessionMode Mode { get { return SessionMode.Mock; } }
        public void Configure(string opennessVersion) { /* the mock serves every version */ }
        public ITiaSession Create() { return new MockTiaSession(); }
    }
}
