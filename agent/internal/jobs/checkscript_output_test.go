package jobs

import "testing"

func TestCleanOutputIsValidUTF8WithinTheLimit(t *testing.T) {
	if out := cleanOutput([]byte("\xEF\xBB\xBFé\x00é"), 4); out != "éé" {
		t.Fatalf("the byte order mark and NUL characters must go: %q", out)
	}
	if out := cleanOutput([]byte("a\xffb"), 100); out != "a�b" {
		t.Fatalf("invalid UTF-8 must be replaced: %q", out)
	}
	if cut := cleanOutput([]byte("aé"), 2); cut != "a" {
		t.Fatalf("a character split by the limit must be dropped, got %q", cut)
	}
	if none := cleanOutput([]byte("abc"), 0); none != "" {
		t.Fatalf("no room means no output, got %q", none)
	}
}

func TestStderrGetsWhatIsLeftOfTheLimit(t *testing.T) {
	stdout := &limitedBuffer{limit: 10}
	stderr := &limitedBuffer{limit: 10}
	_, _ = stdout.Write([]byte("123456"))
	_, _ = stderr.Write([]byte("abcdefghijkl"))

	out := ScriptOutput(stdout, stderr, 10)

	if out.GetStdout() != "123456" || out.GetStderr() != "abcd" || out.GetTotalBytes() != 18 || out.GetLimitBytes() != 10 {
		t.Fatalf("unexpected output %+v", out)
	}
}
