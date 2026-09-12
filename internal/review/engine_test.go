package review

import "testing"

func TestMergeDropsLowConfidenceAndDeduplicates(t *testing.T) {
	got := merge([]Result{{Findings: []Finding{{ID: "a", Path: "x.go", Line: 4, Confidence: .99, Severity: "high"}, {ID: "b", Confidence: .5}}}, {Findings: []Finding{{ID: "a", Path: "x.go", Line: 4, Confidence: .99, Severity: "high"}, {ID: "c", Confidence: .8}}}})
	if len(got.Findings) != 2 {
		t.Fatalf("findings = %d, want 2", len(got.Findings))
	}
}
