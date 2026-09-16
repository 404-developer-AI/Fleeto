package jobs

import (
	"testing"
)

func TestSessionsAreGroupedPerUserWithTheConsoleSessionFirst(t *testing.T) {
	users := groupSessions([]userSession{
		{userID: "S-1-5-21-1-1002", account: `CONTOSO\piet`, session: "3"},
		{userID: "S-1-5-21-1-1001", account: `CONTOSO\jan`, session: "2"},
		{userID: "S-1-5-21-1-1002", account: `CONTOSO\piet`, session: "1", console: true},
		{userID: "", account: "unknown", session: "4"},
	})
	if len(users) != 2 {
		t.Fatalf("expected two users, got %v", users)
	}
	if users[0].GetAccount() != `CONTOSO\jan` || users[1].GetAccount() != `CONTOSO\piet` {
		t.Fatalf("expected the users ordered by account, got %v", users)
	}
	piet := users[1].GetSessions()
	if len(piet) != 2 || piet[0].GetId() != "1" || !piet[0].GetConsole() || piet[1].GetId() != "3" {
		t.Fatalf("expected the console session first, got %v", piet)
	}
}

func TestTheReportedListIsBounded(t *testing.T) {
	var sessions []userSession
	for i := range maxReportedUsers + 10 {
		id := "S-1-5-21-" + string(rune('a'+i%26)) + string(rune('a'+i/26%26)) + string(rune('a'+i/676%26))
		sessions = append(sessions, userSession{userID: id, account: id, session: id})
	}
	if got := len(groupSessions(sessions)); got != maxReportedUsers {
		t.Fatalf("expected %d users, got %d", maxReportedUsers, got)
	}
}

func TestAChosenUserMatchesOnlyItsOwnId(t *testing.T) {
	if !sameUser("s-1-5-21-1-1001", "S-1-5-21-1-1001") || !sameUser("1000", "1000") {
		t.Fatal("the same user was not recognised")
	}
	if sameUser("1000", "10000") || sameUser("S-1-5-21-1-1001", "S-1-5-21-1-1002") {
		t.Fatal("another user was taken for the chosen one")
	}
}
