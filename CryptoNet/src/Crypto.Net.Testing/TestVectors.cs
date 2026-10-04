namespace Crypto.Net.Testing;

/// <summary>
/// Published test vectors (BIP-32/39/84/86, SLIP-10, EIP-55/155/191/712, Web3 Secret Storage, Substrate).
/// Never use these keys with real funds.
/// </summary>
public static class TestVectors
{
    // ---- BIP-39 (trezor/python-mnemonic vectors) ----
    public const string Bip39TestMnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    /// <summary>Seed of <see cref="Bip39TestMnemonic"/> with an empty passphrase.</summary>
    public const string Bip39ExpectedSeedHex = "5eb00bbddcf069084889a8ab9155568165f5c453ccb85e70811aaed6f6da5fc19a5ac40b389cd370d086206dec8aa6c43daea6690f20ad3d8d48b2d2ce9e38e4";

    /// <summary>Seed of <see cref="Bip39TestMnemonic"/> with passphrase "TREZOR".</summary>
    public const string Bip39TrezorSeedHex = "c55257c360c07c72029aebc1b53c05ed0362ada38ead3e3e9efa3708e53495531f09a6987599d18264c1e1c92f2cf141630c7a3c4ab7c81b2f001698e7463b04";

    // ---- Addresses derived from Bip39TestMnemonic ----
    public const string AbandonEthereum = "0x9858EfFD232B4033E47d90003D41EC34EcaEda94";       // m/44'/60'/0'/0/0
    public const string AbandonBitcoinLegacy = "1LqBGSKuX5yYUonjxT5qGfpUsXKYYWeabA";       // m/44'/0'/0'/0/0
    public const string AbandonBitcoinSegwit = "bc1qcr8te4kr609gcawutmrza0j4xv80jy8z306fyu"; // m/84'/0'/0'/0/0 (BIP-84)
    public const string AbandonBitcoinTaproot = "bc1p5cyxnuxmeuwuvkwfem96lqzszd02n6xdcjrs20cac6yqjjwudpxqkedrcr"; // m/86'/0'/0'/0/0 (BIP-86)
    public const string AbandonBip84Zpub = "zpub6rFR7y4Q2AijBEqTUquhVz398htDFrtymD9xYYfG1m4wAcvPhXNfE3EfH1r1ADqtfSdVCToUG868RvUUkgDKf31mGDtKsAYz2oz2AGutZYs";
    public const string AbandonCosmos = "cosmos19rl4cm2hmr8afy4kldpxz3fka4jguq0auqdal4";       // m/44'/118'/0'/0/0
    public const string AbandonSolana = "HAgk14JpMQLgt6rVgv7cBQFJWFto5Dqxi472uT3DKpqk";        // m/44'/501'/0'/0' (Phantom)

    // ---- BIP-32 test vector 1 ----
    public const string Bip32Vector1Seed = "000102030405060708090a0b0c0d0e0f";
    public const string Bip32Vector1MasterXprv = "xprv9s21ZrQH143K3QTDL4LXw2F7HEK3wJUD2nW2nRk4stbPy6cq3jPPqjiChkVvvNKmPGJxWUtg6LnF5kejMRNNU3TGtRBeJgk33yuGBxrMPHi";
    public const string Bip32Vector1MasterXpub = "xpub661MyMwAqRbcFtXgS5sYJABqqG9YLmC4Q1Rdap9gSE8NqtwybGhePY2gZ29ESFjqJoCu1Rupje8YtGqsefD265TMg7usUDFdp6W1EGMcet8";
    public const string Bip32Vector1LeafPath = "m/0'/1/2'/2/1000000000";
    public const string Bip32Vector1LeafXprv = "xprvA41z7zogVVwxVSgdKUHDy1SKmdb533PjDz7J6N6mV6uS3ze1ai8FHa8kmHScGpWmj4WggLyQjgPie1rFSruoUihUZREPSL39UNdE3BBDu76";

    // ---- SLIP-10 ed25519 test vector 1 ----
    public const string Slip10Vector1LeafKey = "8f94d394a8e8fd6b1bc2f3f49f5c47e385281d5c17e65324b0f62483e37e8793"; // m/0'/1'/2'/2'/1000000000'

    // ---- Ethereum ----
    public const string EthValidChecksum1 = "0x5aAeb6053F3E94C9b9A09f33669435E7Ef1BeAed";
    public const string EthValidChecksum2 = "0xfB6916095ca1df60bB79Ce92cE3Ea74c37c5d359";
    public const string EthValidChecksum3 = "0xdbF03B407c01E7cD3CBea99509d93f8DDDC8C6FB";

    /// <summary>EIP-155 example: nonce 9, 20 gwei, 21000 gas, to 0x3535…, 1 ETH, chain 1, key 0x4646….</summary>
    public const string Eip155PrivateKey = "4646464646464646464646464646464646464646464646464646464646464646";
    public const string Eip155SignedTx = "f86c098504a817c800825208943535353535353535353535353535353535353535880de0b6b3a76400008025a028ef61340bd939bc2195fe537567866003e1a15d3c71ff63e1590620aa636276a067cbe9d8997f761aecb703304b3800ccf555c9f3dc64214b297fb1966a3b6d83";

    /// <summary>web3.js <c>accounts.sign("Some data", key)</c> example.</summary>
    public const string PersonalSignPrivateKey = "4c0883a69102937d6231471b5dbb6204fe5129617082792ae468d01a3f362318";
    public const string PersonalSignAddress = "0x2c7536E3605D9C16a7a3D7b1898e529396a65c23";
    public const string PersonalSignSignature = "0xb91467e570a6466aa9e9876cbcd013baba02900b8979d43fe208a4a4f339f5fd6007e74cd82e037b800186422fc2da167c747ef045e5d18a5f5d4300f8e1a0291c";

    /// <summary>EIP-712 "Mail" example from the specification; signer key = keccak256("cow").</summary>
    public const string Eip712MailJson = """
    {
      "types": {
        "EIP712Domain": [
          { "name": "name", "type": "string" },
          { "name": "version", "type": "string" },
          { "name": "chainId", "type": "uint256" },
          { "name": "verifyingContract", "type": "address" }
        ],
        "Person": [
          { "name": "name", "type": "string" },
          { "name": "wallet", "type": "address" }
        ],
        "Mail": [
          { "name": "from", "type": "Person" },
          { "name": "to", "type": "Person" },
          { "name": "contents", "type": "string" }
        ]
      },
      "primaryType": "Mail",
      "domain": {
        "name": "Ether Mail",
        "version": "1",
        "chainId": 1,
        "verifyingContract": "0xCcCCccccCCCCcCCCCCCcCcCccCcCCCcCcccccccC"
      },
      "message": {
        "from": { "name": "Cow", "wallet": "0xCD2a3d9F938E13CD947Ec05AbC7FE734Df8DD826" },
        "to": { "name": "Bob", "wallet": "0xbBbBBBBbbBBBbbbBbbBbbbbBBbBbbbbBbBbbBBbB" },
        "contents": "Hello, Bob!"
      }
    }
    """;
    public const string Eip712MailDigest = "be609aee343fb3c4b28e1df9e632fca64fcfaede20f02e86244efddf30957bd2";
    public const string Eip712MailSignerAddress = "0xCD2a3d9F938E13CD947Ec05AbC7FE734Df8DD826";

    // ---- Web3 Secret Storage v3 (password "testpassword", private key below) ----
    public const string KeystorePrivateKey = "7a28b5ba57c53603b0b07b56bba752f7784bf506fa95edc395f5cf6c7514fe9d";
    public const string KeystorePassword = "testpassword";
    public const string KeystorePbkdf2Json = """
    {"crypto":{"cipher":"aes-128-ctr","cipherparams":{"iv":"6087dab2f9fdbbfaddc31a909735c1e6"},"ciphertext":"5318b4d5bcd28de64ee5559e671353e16f075ecae9f99c7a79a38af5f869aa46","kdf":"pbkdf2","kdfparams":{"c":262144,"dklen":32,"prf":"hmac-sha256","salt":"ae3cd4e7013836a3df6bd7241b12db061dbe2c6785853cce422d148a624ce0bd"},"mac":"517ead924a9d0dc3124507e3393d175ce3ff7c1e96529c6c555ce9e51205e9b2"},"id":"3198bc9c-6672-5ab3-d995-4942343ae5b6","version":3}
    """;
    public const string KeystoreScryptJson = """
    {"crypto":{"cipher":"aes-128-ctr","cipherparams":{"iv":"83dbcc02d8ccb40e466191a123791e0e"},"ciphertext":"d172bf743a674da9cdad04534d56926ef8358534d458fffccd4e6ad2fbde479c","kdf":"scrypt","kdfparams":{"dklen":32,"n":262144,"p":8,"r":1,"salt":"ab0c7876052600dd703518d6fc3fe8984592145b591fc8fb5c6d43190334ba19"},"mac":"2103ac29920d71da29f15d75b4a16dbe95cfd7ff8faea1056c33131d846e3097"},"id":"3198bc9c-6672-5ab3-d995-4942343ae5b6","version":3}
    """;

    // ---- Bitcoin ----
    public const string BtcMainnetSegwit = "bc1qw508d6qejxtdg4y5r3zarvary0c5xw7kv8f3t4";
    public const string WifPrivateKey = "0C28FCA386C7A227600B2FE50B7CAE11EC86D3BF1FBE471BE89827E19D72AA1D";
    public const string WifCompressed = "KwdMAjGmerYanjeui5SHS7JkmpZvVipYvB2LJGU1ZxJwYvP98617";

    // ---- Solana ----
    public const string SolanaTokenProgram = "TokenkegQfeZyiNwAJbNbGKPFXCWuBvf9Ss623VQ5DA";
    public const string SolanaSystemProgram = "11111111111111111111111111111111";

    // ---- Substrate (DEV_PHRASE) ----
    public const string AliceSr25519PublicKey = "d43593c715fdd31c61141abd04a99fd6822c8558854ccde39a5684e7a56da27d";
    public const string AliceSs58Generic = "5GrwvaEF5zXb26Fz9rcQpDWS57CtERHpNehXCPcNoHGKutQY";
    public const string AliceSs58Polkadot = "15oF4uVJwmo4TdGW7VfQxNLavjCXviqxT9S1MgbjMNHr6Sp5";
    public const string AliceEd25519Ss58Generic = "5FA9nQDVg267DEd8m1ZypXLBnvN7SFxYwV7ndqSYGiN9TTpu";
}
