// Editor/Asphodel/Inference/IPatternAnalyzer.cs
using System;
using System.Collections.Generic;
using ArcForge.Hades.Editor.Charon;

namespace ArcForge.Hades.Editor.Asphodel.Inference
{
    public interface IPatternAnalyzer
    {
        string Name { get; }
        bool IsEnabled(InferenceConfig config);
        List<InferredPattern> Analyze(
            List<TraceRecord> traces,
            List<SpanRecord> spans,
            DateTimeOffset since
        );
    }
}
