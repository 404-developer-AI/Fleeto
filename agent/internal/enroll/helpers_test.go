package enroll

import (
	"crypto/rand"
	"errors"

	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
)

func asError(err error, target **Error) bool { return errors.As(err, target) }

func createCSRForTest(req Request) ([]byte, error) {
	return keystore.CreateCSR(rand.Reader, req.Key, "foreign")
}
