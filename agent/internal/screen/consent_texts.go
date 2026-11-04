package screen

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"strings"
	"sync/atomic"
	"time"
	"unicode"
)

// consentLogger is where a prompt that fell back to the plain one says why; the agent sets it once at start.
var consentLogger atomic.Pointer[slog.Logger]

// SetConsentLogger sets the logger of the consent prompt.
func SetConsentLogger(logger *slog.Logger) { consentLogger.Store(logger) }

func consentLog() *slog.Logger {
	if l := consentLogger.Load(); l != nil {
		return l
	}
	return slog.New(slog.DiscardHandler)
}

// The texts of the consent prompt (0.8.0, decided 2026-11-04): set per policy and carried in the signed token, so a client can have its own
// words and language. An empty text is the English default below. The prompt always names the technician and shows the seconds left
// itself, outside these texts, so a policy can never hide who is asking. The limits match ConsentTextRules of the server; the agent cuts
// longer texts itself and never trusts their length.
const (
	ConsentMaxTitle        = 60
	ConsentMaxMessage      = 400
	ConsentMaxMessageLines = 8
	ConsentMaxLabel        = 20

	ConsentDefaultTitle   = "Remote control request"
	ConsentDefaultMessage = "wants to view and control this computer through Fleeto remote control. Allow the session?"
	ConsentDefaultAllow   = "Allow"
	ConsentDefaultDeny    = "Deny"
)

// ConsentTexts are the words of the consent prompt.
type ConsentTexts struct {
	Title   string `json:"title,omitempty"`
	Message string `json:"message,omitempty"`
	Allow   string `json:"allow,omitempty"`
	Deny    string `json:"deny,omitempty"`
}

// Effective is the texts the prompt shows: cleaned of control characters, cut to the limits, and the default for every empty text. Two
// labels that read the same fall back to the defaults, so the person at the screen can always tell the buttons apart.
func (t ConsentTexts) Effective() ConsentTexts {
	out := ConsentTexts{
		Title:   orDefault(consentLine(t.Title, ConsentMaxTitle), ConsentDefaultTitle),
		Message: orDefault(consentLines(t.Message, ConsentMaxMessage, ConsentMaxMessageLines), ConsentDefaultMessage),
		Allow:   orDefault(consentLine(t.Allow, ConsentMaxLabel), ConsentDefaultAllow),
		Deny:    orDefault(consentLine(t.Deny, ConsentMaxLabel), ConsentDefaultDeny),
	}
	if strings.EqualFold(out.Allow, out.Deny) {
		out.Allow, out.Deny = ConsentDefaultAllow, ConsentDefaultDeny
	}
	return out
}

// IsDefaultMessage reports whether the message is the default one, which the prompt follows with the English timeout sentence.
func (t ConsentTexts) IsDefaultMessage() bool { return t.Message == ConsentDefaultMessage }

// ConsentTechnician is the name the prompt shows for the technician: one line, cut, never empty.
func ConsentTechnician(technician string) string {
	return orDefault(consentLine(technician, 100), "A technician")
}

// ConsentPlainText is the whole question as one text, for a prompt that cannot lay it out (the Windows message box of the fallback): the
// technician, the message and what happens without an answer.
func ConsentPlainText(technician string, texts ConsentTexts, seconds int) string {
	texts = texts.Effective()
	return fmt.Sprintf("%s %s\n\nWithout an answer within %d seconds, access is granted.", ConsentTechnician(technician), texts.Message, seconds)
}

// ConsentTimeLeft is the line under the countdown: with the default message the English sentence, with a message of the policy only the
// seconds, so a prompt in another language holds no English sentence (the message of the policy says what happens).
func ConsentTimeLeft(texts ConsentTexts, seconds int) string {
	if texts.IsDefaultMessage() {
		return fmt.Sprintf("Access is granted in %d s without an answer.", seconds)
	}
	return fmt.Sprintf("%d s", seconds)
}

func orDefault(value, fallback string) string {
	if value == "" {
		return fallback
	}
	return value
}

// consentLine is one line: control characters become spaces, runs of spaces one, trimmed and cut to max runes.
func consentLine(value string, max int) string {
	value = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) || r == ' ' || r == ' ' {
			return ' '
		}
		return r
	}, value)
	value = strings.Join(strings.Fields(value), " ")
	return cutRunes(value, max)
}

// consentLines keeps line breaks (at most maxLines lines, no empty first or last line, at most one empty line in a row) and cuts the
// whole text to max runes.
func consentLines(value string, max, maxLines int) string {
	value = strings.NewReplacer("\r\n", "\n", "\r", "\n").Replace(value)
	var kept []string
	for _, raw := range strings.Split(value, "\n") {
		line := consentLine(raw, max)
		if line == "" && (len(kept) == 0 || kept[len(kept)-1] == "") {
			continue
		}
		kept = append(kept, line)
	}
	for len(kept) > 0 && kept[len(kept)-1] == "" {
		kept = kept[:len(kept)-1]
	}
	if len(kept) > maxLines {
		kept = kept[:maxLines]
	}
	return strings.TrimSpace(cutRunes(strings.Join(kept, "\n"), max))
}

func cutRunes(value string, max int) string {
	runes := []rune(value)
	if len(runes) <= max {
		return value
	}
	return strings.TrimSpace(string(runes[:max]))
}

// consentChild is a running "fleeto-agent remote-consent" process.
type consentChild interface {
	In() io.Writer
	Out() io.Reader
}

// askConsentChild hands the question to a running consent process and waits for its answer. The process counts down itself; the agent
// gives it 10 seconds more before it gives up on it.
func askConsentChild(ctx context.Context, child consentChild, ask ConsentAskBody, timeout time.Duration) (ConsentAnswer, error) {
	data, _ := json.Marshal(ask)
	if err := WriteFrame(child.In(), append([]byte{FrameConsentAsk}, data...)); err != nil {
		return ConsentRefused, err
	}
	answers := make(chan ConsentAnswerBody, 1)
	go func() {
		frame, err := ReadFrame(child.Out())
		var answer ConsentAnswerBody
		if err == nil && len(frame) > 0 && frame[0] == FrameConsentAnswer {
			_ = json.Unmarshal(frame[1:], &answer)
		}
		answers <- answer
	}()
	select {
	case <-ctx.Done():
		return ConsentRefused, ctx.Err()
	case <-time.After(timeout + 10*time.Second):
		return ConsentRefused, errors.New("the consent prompt did not answer")
	case answer := <-answers:
		return parseConsentAnswer(answer.Answer)
	}
}

func parseConsentAnswer(answer string) (ConsentAnswer, error) {
	switch answer {
	case "allowed":
		return ConsentAllowed, nil
	case "timeout":
		return ConsentTimedOut, nil
	case "refused":
		return ConsentRefused, nil
	}
	return ConsentRefused, errors.New("the consent prompt could not be shown on the screen")
}

// readConsentAsk reads the question in the consent process; its seconds are held inside the bounds of the policy.
func readConsentAsk(in io.Reader) (ConsentAskBody, error) {
	frame, err := ReadFrame(in)
	if err != nil {
		return ConsentAskBody{}, err
	}
	if len(frame) == 0 || frame[0] != FrameConsentAsk {
		return ConsentAskBody{}, errors.New("expected the consent question")
	}
	var ask ConsentAskBody
	if err := json.Unmarshal(frame[1:], &ask); err != nil {
		return ConsentAskBody{}, err
	}
	ask.Seconds = min(max(ask.Seconds, 10), 300)
	ask.Texts = ask.Texts.Effective()
	ask.Technician = ConsentTechnician(ask.Technician)
	return ask, nil
}

func writeConsentAnswer(out io.Writer, answer string) error {
	data, _ := json.Marshal(ConsentAnswerBody{Answer: answer})
	return WriteFrame(out, append([]byte{FrameConsentAnswer}, data...))
}
