package connect

import (
	"bytes"
	"context"
	"crypto/ecdh"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"time"
)

// ClaimPath is where Fleeto web takes a claim (PROXMOX-WEB.md §3, step 3).
const ClaimPath = "/connect/v1/claim"

// claimWait covers web waiting for fleeto-signer (20 seconds) with room.
const claimWait = 45 * time.Second

// Ticket is what web returns for a claim: the signed token of the session and the keys the agent may sign its session key with.
type Ticket struct {
	ParticipantID string   `json:"participantId"`
	SessionID     string   `json:"sessionId"`
	Token         string   `json:"token"`
	Signature     string   `json:"signature"`
	KeyID         string   `json:"keyId"`
	Fingerprints  []string `json:"fingerprints"`
	// RelayURL is the relay address including the participant id.
	RelayURL string `json:"relayUrl"`
	// Endpoint is the host name of the endpoint, for messages and the local address.
	Endpoint string `json:"endpoint"`
	// EndpointID derives the loopback host name, so the Proxmox sign-in of one host never mixes with another's.
	EndpointID string `json:"endpointId"`
}

type claimRequest struct {
	Claim     string `json:"claim"`
	PublicKey string `json:"publicKey"`
	Version   string `json:"version"`
	Platform  string `json:"platform"`
}

type problem struct {
	Title  string `json:"title"`
	Detail string `json:"detail"`
}

// Claim posts the claim with a new ephemeral key and returns the ticket and the private key that belongs to it.
func Claim(ctx context.Context, client *http.Client, link Link, version, platform string) (*Ticket, *ecdh.PrivateKey, error) {
	private, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return nil, nil, fmt.Errorf("generate the session key: %w", err)
	}
	body, _ := json.Marshal(claimRequest{
		Claim: link.Claim, PublicKey: base64.StdEncoding.EncodeToString(private.PublicKey().Bytes()), Version: version, Platform: platform,
	})
	ctx, cancel := context.WithTimeout(ctx, claimWait)
	defer cancel()
	request, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://"+link.Instance+ClaimPath, bytes.NewReader(body))
	if err != nil {
		return nil, nil, err
	}
	request.Header.Set("Content-Type", "application/json")
	request.Header.Set("User-Agent", "FleetoConnect/"+version)
	response, err := client.Do(request)
	if err != nil {
		return nil, nil, fmt.Errorf("Fleeto Connect could not reach %s: %w", link.Instance, err)
	}
	defer response.Body.Close()
	data, err := io.ReadAll(io.LimitReader(response.Body, 64*1024))
	if err != nil {
		return nil, nil, err
	}
	if response.StatusCode != http.StatusOK {
		var p problem
		if json.Unmarshal(data, &p) == nil && p.Detail != "" {
			return nil, nil, errors.New(p.Detail)
		}
		return nil, nil, fmt.Errorf("%s refused the session (HTTP %d)", link.Instance, response.StatusCode)
	}
	var ticket Ticket
	if err := json.Unmarshal(data, &ticket); err != nil {
		return nil, nil, fmt.Errorf("the answer of %s cannot be read: %w", link.Instance, err)
	}
	if !strings.EqualFold(ticket.ParticipantID, link.ClaimID) || ticket.Token == "" || ticket.Signature == "" || len(ticket.Fingerprints) == 0 {
		return nil, nil, fmt.Errorf("the answer of %s is not for this session", link.Instance)
	}
	if err := checkRelay(link, ticket.RelayURL); err != nil {
		return nil, nil, err
	}
	return &ticket, private, nil
}

// checkRelay accepts a relay only on the instance itself (wss), or plain ws on a development instance on this PC.
func checkRelay(link Link, relay string) error {
	u, err := url.Parse(relay)
	if err != nil {
		return errors.New("the instance named no valid relay")
	}
	host := u.Hostname()
	instanceHost := link.Instance
	if h, _, splitErr := net.SplitHostPort(instanceHost); splitErr == nil {
		instanceHost = h
	}
	switch {
	case u.Scheme == "wss" && strings.EqualFold(host, instanceHost):
		return nil
	case u.Scheme == "ws" && link.isLocal() && host == "localhost":
		return nil
	default:
		return fmt.Errorf("the instance named a relay that is not on %s", link.Instance)
	}
}
