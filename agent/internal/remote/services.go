package remote

import (
	"context"
	"errors"
	"fmt"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
)

// Services in the remote background window (0.3.0 step 2): the endpoint lists its services and starts, stops, restarts or changes the
// start type of one, as SYSTEM or root. Actions are audited; listing is not.

const serviceActionTimeout = 60 * time.Second

type serviceItem struct {
	Name        string `json:"name"`
	DisplayName string `json:"displayName"`
	StartType   string `json:"startType"`
	State       string `json:"state"`
}

func (b *background) services() (map[string]any, error) {
	items, err := inventory.Services()
	if err != nil {
		return nil, fmt.Errorf("the services could not be listed: %w", err)
	}
	out := make([]serviceItem, 0, len(items))
	for _, s := range items {
		out = append(out, serviceItem{Name: s.GetName(), DisplayName: s.GetDisplayName(), StartType: s.GetStartType(), State: s.GetState()})
	}
	return map[string]any{"services": out}, nil
}

func (b *background) serviceAction(ctx context.Context, req requestBody) (map[string]any, error) {
	name, err := serviceName(req.Name)
	if err != nil {
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, serviceActionTimeout)
	defer cancel()

	var detail string
	switch req.Action {
	case "start":
		err = startService(ctx, name)
	case "stop":
		err = stopService(ctx, name)
	case "restart":
		err = restartService(ctx, name)
	case "start_type":
		if err = setStartType(ctx, name, req.StartType); err == nil {
			detail = req.StartType
		}
	default:
		return nil, fmt.Errorf("%q is not a service action", req.Action)
	}
	if err != nil {
		return nil, err
	}
	b.report("service."+req.Action, name, detail)
	state, _ := serviceState(ctx, name)
	return map[string]any{"state": state}, nil
}

func serviceName(name string) (string, error) {
	if name == "" || len(name) > 256 {
		return "", errors.New("choose a service")
	}
	for _, r := range name {
		// A service or unit name is letters, digits and a few punctuation marks; never a space, slash or control character.
		if r <= ' ' || r == '/' || r == '\\' || r == ';' || r == '&' || r == '|' || r == '\'' || r == '"' {
			return "", errors.New("that is not a valid service name")
		}
	}
	return name, nil
}
