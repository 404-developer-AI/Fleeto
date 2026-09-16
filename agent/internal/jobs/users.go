package jobs

import (
	"errors"
	"sort"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// ErrUserNotSignedIn is returned when a job names the user it runs as and that user has no active session (anymore). The job fails
// with this reason; it never runs as another user.
var ErrUserNotSignedIn = errors.New("the chosen user is not signed in on this endpoint")

// maxReportedUsers bounds the list the agent reports; a terminal server with more users reports the first ones by account.
const maxReportedUsers = 200

// userSession is one active session with the account signed in to it, as the platform lists them.
type userSession struct {
	userID  string
	account string
	session string
	console bool
}

// groupSessions turns the sessions into the reported users: one entry per account, ordered by account name, its console session first.
func groupSessions(sessions []userSession) []*agentv1.SignedInUser {
	byID := map[string]*agentv1.SignedInUser{}
	var users []*agentv1.SignedInUser
	for _, s := range sessions {
		if s.userID == "" {
			continue
		}
		user, ok := byID[s.userID]
		if !ok {
			user = &agentv1.SignedInUser{Id: s.userID, Account: s.account}
			byID[s.userID] = user
			users = append(users, user)
		}
		user.Sessions = append(user.Sessions, &agentv1.UserSession{Id: s.session, Console: s.console})
	}
	for _, user := range users {
		sort.SliceStable(user.Sessions, func(i, j int) bool { return user.Sessions[i].Console && !user.Sessions[j].Console })
	}
	sort.SliceStable(users, func(i, j int) bool {
		a, b := strings.ToLower(users[i].Account), strings.ToLower(users[j].Account)
		if a != b {
			return a < b
		}
		return users[i].Id < users[j].Id
	})
	if len(users) > maxReportedUsers {
		users = users[:maxReportedUsers]
	}
	return users
}

// sameUser compares a user id of a job with one of a session: SIDs are case-insensitive, uids are digits.
func sameUser(chosen, userID string) bool {
	return strings.EqualFold(chosen, userID)
}
