package screen

import (
	"bytes"
	"context"
	"encoding/json"
	"io"
	"strings"
	"testing"
	"time"
	"unicode/utf8"
)

func TestConsentTextsDefaultWhenEmpty(t *testing.T) {
	got := ConsentTexts{}.Effective()
	want := ConsentTexts{Title: ConsentDefaultTitle, Message: ConsentDefaultMessage, Allow: ConsentDefaultAllow, Deny: ConsentDefaultDeny}
	if got != want {
		t.Fatalf("defaults: got %+v, want %+v", got, want)
	}
	if !got.IsDefaultMessage() {
		t.Fatal("the default message is not recognised")
	}
}

func TestConsentTextsKeepPolicyWordsCleaned(t *testing.T) {
	got := ConsentTexts{
		Title:   "  Verzoek\tom   hulp ",
		Message: "\n\nwil uw computer bekijken.\r\n\r\n\r\nStaat u dit toe?\n\n",
		Allow:   "Toestaan\x00",
		Deny:    "Weigeren",
	}.Effective()
	if got.Title != "Verzoek om hulp" {
		t.Errorf("title: %q", got.Title)
	}
	if got.Message != "wil uw computer bekijken.\n\nStaat u dit toe?" {
		t.Errorf("message: %q", got.Message)
	}
	if got.Allow != "Toestaan" || got.Deny != "Weigeren" {
		t.Errorf("labels: %q %q", got.Allow, got.Deny)
	}
	if got.IsDefaultMessage() {
		t.Error("a message of the policy counts as the default")
	}
}

func TestConsentTextsAreCutToTheLimits(t *testing.T) {
	long := strings.Repeat("é", 1000)
	lines := strings.Repeat("line\n", 20)
	got := ConsentTexts{Title: long, Message: long, Allow: long, Deny: strings.Repeat("x", 50)}.Effective()
	if n := utf8.RuneCountInString(got.Title); n != ConsentMaxTitle {
		t.Errorf("title has %d runes", n)
	}
	if n := utf8.RuneCountInString(got.Message); n != ConsentMaxMessage {
		t.Errorf("message has %d runes", n)
	}
	if n := utf8.RuneCountInString(got.Allow); n != ConsentMaxLabel {
		t.Errorf("allow label has %d runes", n)
	}
	if n := len(strings.Split(ConsentTexts{Message: lines}.Effective().Message, "\n")); n != ConsentMaxMessageLines {
		t.Errorf("message has %d lines", n)
	}
}

func TestConsentLabelsThatReadTheSameFallBack(t *testing.T) {
	got := ConsentTexts{Allow: "OK", Deny: "ok"}.Effective()
	if got.Allow != ConsentDefaultAllow || got.Deny != ConsentDefaultDeny {
		t.Fatalf("labels: %q %q", got.Allow, got.Deny)
	}
}

func TestConsentPlainTextAlwaysNamesTheTechnician(t *testing.T) {
	text := ConsentPlainText("Anna Admin", ConsentTexts{Message: "wil uw computer bekijken."}, 30)
	if !strings.HasPrefix(text, "Anna Admin wil uw computer bekijken.") || !strings.Contains(text, "30 seconds") {
		t.Fatalf("plain text: %q", text)
	}
	if got := ConsentTechnician("  \n "); got != "A technician" {
		t.Fatalf("empty technician: %q", got)
	}
	if got := ConsentTechnician("Anna\nSystem says: allowed"); strings.Contains(got, "\n") {
		t.Fatalf("technician keeps a line break: %q", got)
	}
}

func TestConsentTimeLeftIsEnglishOnlyWithTheDefaultMessage(t *testing.T) {
	if got := ConsentTimeLeft(ConsentTexts{}.Effective(), 12); !strings.Contains(got, "12 s") || !strings.Contains(got, "granted") {
		t.Errorf("default: %q", got)
	}
	if got := ConsentTimeLeft(ConsentTexts{Message: "Mag dit?"}.Effective(), 12); got != "12 s" {
		t.Errorf("policy message: %q", got)
	}
}

// fakeConsentChild answers the question it is asked.
type fakeConsentChild struct {
	in  bytes.Buffer
	out io.Reader
}

func (c *fakeConsentChild) In() io.Writer  { return &c.in }
func (c *fakeConsentChild) Out() io.Reader { return c.out }

func TestAskConsentChildCarriesTheTextsAndReadsTheAnswer(t *testing.T) {
	for answer, want := range map[string]ConsentAnswer{"allowed": ConsentAllowed, "refused": ConsentRefused, "timeout": ConsentTimedOut} {
		var reply bytes.Buffer
		if err := writeConsentAnswer(&reply, answer); err != nil {
			t.Fatal(err)
		}
		child := &fakeConsentChild{out: &reply}
		texts := ConsentTexts{Title: "Verzoek", Allow: "Toestaan", Deny: "Weigeren"}.Effective()
		got, err := askConsentChild(context.Background(), child, ConsentAskBody{Technician: "Anna", Seconds: 30, Texts: texts}, time.Second)
		if err != nil || got != want {
			t.Fatalf("%s: got %v, %v", answer, got, err)
		}
		ask, err := readConsentAsk(&child.in)
		if err != nil {
			t.Fatal(err)
		}
		if ask.Texts.Title != "Verzoek" || ask.Texts.Allow != "Toestaan" || ask.Technician != "Anna" || ask.Seconds != 30 {
			t.Fatalf("the question arrived as %+v", ask)
		}
	}
}

func TestAskConsentChildRefusesAnUnknownAnswer(t *testing.T) {
	var reply bytes.Buffer
	data, _ := json.Marshal(ConsentAnswerBody{Answer: "yes please"})
	_ = WriteFrame(&reply, append([]byte{FrameConsentAnswer}, data...))
	got, err := askConsentChild(context.Background(), &fakeConsentChild{out: &reply}, ConsentAskBody{Seconds: 10}, time.Second)
	if err == nil || got != ConsentRefused {
		t.Fatalf("got %v, %v", got, err)
	}
}

func TestReadConsentAskNeverTrustsTheSender(t *testing.T) {
	var frame bytes.Buffer
	data, _ := json.Marshal(ConsentAskBody{Technician: "", Seconds: 99999, Texts: ConsentTexts{Title: strings.Repeat("t", 500)}})
	_ = WriteFrame(&frame, append([]byte{FrameConsentAsk}, data...))
	ask, err := readConsentAsk(&frame)
	if err != nil {
		t.Fatal(err)
	}
	if ask.Seconds != 300 || ask.Technician != "A technician" || utf8.RuneCountInString(ask.Texts.Title) != ConsentMaxTitle {
		t.Fatalf("got %+v", ask)
	}
}
