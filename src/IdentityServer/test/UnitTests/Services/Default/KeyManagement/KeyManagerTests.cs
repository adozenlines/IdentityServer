
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Duende.IdentityServer.Configuration;
using Duende.IdentityServer.Extensions;
using Duende.IdentityServer.Models;
using Duende.IdentityServer.Services.KeyManagement;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace UnitTests.Services.Default.KeyManagement
{
    public class KeyManagerTests
    {
        KeyManager _subject;

        KeyManagementOptions _options = new KeyManagementOptions()
        {
            // just to speed up the tests
            InitializationSynchronizationDelay = TimeSpan.FromSeconds(1)
        };

        MockSigningKeyStore _mockKeyStore = new MockSigningKeyStore();
        MockSigningKeyStoreCache _mockKeyStoreCache = new MockSigningKeyStoreCache();
        MockSigningKeyProtector _mockKeyProtector = new MockSigningKeyProtector();
        MockClock _mockClock = new MockClock(new DateTime(2018, 3, 10, 9, 0, 0));

        public KeyManagerTests()
        {
            _subject = new KeyManager(
                _options,
                _mockKeyStore, 
                _mockKeyStoreCache,
                _mockKeyProtector, 
                _mockClock,
                new NopKeyLock(),
                new LoggerFactory().CreateLogger<KeyManager>());
        }

        RsaKeyContainer CreateKey(TimeSpan? age = null, KeyType keyType = KeyType.RSA)
        {
            var key = _options.CreateRsaSecurityKey();

            var date = _mockClock.UtcNow.DateTime;
            if (age.HasValue) date = date.Subtract(age.Value);

            var container = keyType == KeyType.RSA ?
                new RsaKeyContainer(key, date) :
                new X509KeyContainer(key, date, _options.KeyRetirement);
            
            return container;
        }

        string CreateAndStoreKey(TimeSpan? age = null)
        {
            var container = CreateKey(age);
            _mockKeyStore.Keys.Add(_mockKeyProtector.Protect(container));
            return container.Id;
        }
        
        string CreateCacheAndStoreKey(TimeSpan? age = null)
        {
            var container = CreateKey(age);
            _mockKeyStore.Keys.Add(_mockKeyProtector.Protect(container));
            _mockKeyStoreCache.Cache.Add(container);
            return container.Id;
        }

        // ctor

        [Fact]
        public void ctor_should_validate_options()
        {
            _options.KeyActivationDelay = TimeSpan.Zero;

            Action a = () =>
            {
                _subject = new KeyManager(
                  _options,
                  _mockKeyStore,
                  _mockKeyStoreCache,
                  _mockKeyProtector,
                  _mockClock,
                  new NopKeyLock(),
                  new LoggerFactory().CreateLogger<KeyManager>());
            };
            a.Should().Throw<Exception>();
        }

        // GetCurrentKeyAsync

        [Fact]
        public async Task GetCurrentKeyAsync_should_return_key()
        {
            var id = CreateAndStoreKey(_options.KeyActivationDelay.Add(TimeSpan.FromHours(1)));

            var key = await _subject.GetCurrentKeyAsync();

            key.Id.Should().Be(id);
        }

        // GetAllKeysInternalAsync

        [Fact]
        public async Task GetAllKeysInternalAsync_when_valid_key_exists_should_use_key()
        {
            var id = CreateAndStoreKey(_options.KeyActivationDelay.Add(TimeSpan.FromHours(1)));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Id.Should().Be(id);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_recently_created_key_exists_should_use_key()
        {
            var id = CreateAndStoreKey(TimeSpan.FromSeconds(5));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            _mockKeyStore.Keys.Count.Should().Be(1);
            _mockKeyStore.Keys.Single().Id.Should().Be(key.Id);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_only_one_key_created_in_future_should_use_key()
        {
            var id = CreateAndStoreKey(-TimeSpan.FromSeconds(5));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            _mockKeyStore.Keys.Count.Should().Be(1);
            _mockKeyStore.Keys.Single().Id.Should().Be(key.Id);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_no_keys_should_create_key()
        {
            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            _mockKeyStore.Keys.Count.Should().Be(1);
            _mockKeyStore.Keys.Single().Id.Should().Be(key.Id);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_null_keys_should_create_key()
        {
            _mockKeyStore.Keys = null;

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            keys.Should().NotBeEmpty();
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_all_keys_are_expired_should_create_key()
        {
            var id = CreateAndStoreKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(5)));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            _mockKeyStore.Keys.Count.Should().Be(2);
            key.Id.Should().NotBe(id);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_all_keys_are_expired_should_requery_database_and_use_valid_db_key()
        {
            var id1 = CreateCacheAndStoreKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(5)));
            var id2 = CreateAndStoreKey();

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            key.Id.Should().Be(id2);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_should_use_oldest_active_key()
        {
            var key1 = CreateAndStoreKey(TimeSpan.FromSeconds(10));
            var key2 = CreateAndStoreKey(TimeSpan.FromSeconds(5));
            var key3 = CreateAndStoreKey(-TimeSpan.FromSeconds(5));
            var key4 = CreateAndStoreKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(5)));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            _mockKeyStore.Keys.Count.Should().Be(4);
            key.Id.Should().Be(key1);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_should_ignore_keys_not_yet_activated()
        {
            var key1 = CreateAndStoreKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(10)));
            var key2 = CreateAndStoreKey(-TimeSpan.FromSeconds(5));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            _mockKeyStore.Keys.Count.Should().Be(2);
            key.Id.Should().Be(key1);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_cache_empty_should_return_non_retired_keys_from_store()
        {
            var key1 = CreateAndStoreKey(TimeSpan.FromSeconds(10));
            var key2 = CreateAndStoreKey(TimeSpan.FromSeconds(5));
            var key3 = CreateAndStoreKey(-TimeSpan.FromSeconds(5));
            var key4 = CreateAndStoreKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(5)));
            var key5 = CreateAndStoreKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(5)));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            keys.Select(x => x.Id).Should().BeEquivalentTo(new[] { key1, key2, key3, key4 });
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_cache_null_should_return_non_retired_keys_from_store()
        {
            _mockKeyStoreCache.Cache = null;

            var key1 = CreateAndStoreKey(TimeSpan.FromSeconds(10));
            var key2 = CreateAndStoreKey(TimeSpan.FromSeconds(5));
            var key3 = CreateAndStoreKey(-TimeSpan.FromSeconds(5));
            var key4 = CreateAndStoreKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(5)));
            var key5 = CreateAndStoreKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(5)));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            keys.Select(x => x.Id).Should().BeEquivalentTo(new[] { key1, key2, key3, key4 });
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_cache_empty_should_update_the_cache()
        {
            var key = CreateAndStoreKey();

            var (keys, _) = await _subject.GetAllKeysInternalAsync();

            keys.Count().Should().Be(1);
            keys.Single().Id.Should().Be(key);
            _mockKeyStoreCache.StoreKeysAsyncWasCalled.Should().BeTrue();
            _mockKeyStoreCache.Cache.Count().Should().Be(1);
            _mockKeyStoreCache.Cache.Single().Id.Should().Be(key);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_should_use_the_cache()
        {
            var key = CreateKey();
            _mockKeyStoreCache.Cache = new List<RsaKeyContainer>()
            {
                key
            };

            var (keys, _) = await _subject.GetAllKeysInternalAsync();

            keys.Count().Should().Be(1);
            keys.Single().Id.Should().Be(key.Id);
            _mockKeyStore.LoadKeysAsyncWasCalled.Should().BeFalse();
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_key_rotation_is_needed_should_create_new_key()
        {
            var key1 = CreateAndStoreKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1)));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Should().NotBeNull();
            key.Id.Should().Be(key1);
            _mockKeyStore.Keys.Count.Should().Be(2);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_key_rotation_is_needed_for_cached_keys_should_requery_database_to_determine_if_rotation_still_needed()
        {
            var key1 = CreateCacheAndStoreKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1)));
            var key2 = CreateAndStoreKey();

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            _mockKeyStore.Keys.Count.Should().Be(2);
        }

        [Fact]
        public async Task GetAllKeysInternalAsync_when_key_rotation_is_not_needed_should_not_create_new_key()
        {
            var key1 = CreateAndStoreKey(_options.KeyExpiration.Subtract(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1))));

            var (keys, key) = await _subject.GetAllKeysInternalAsync();

            key.Id.Should().Be(key1);
            _mockKeyStore.Keys.Count.Should().Be(1);
        }

        // GetKeysFromCacheAsync

        [Fact]
        public async Task GetKeysFromCacheAsync_should_use_cache()
        {
            var id = CreateCacheAndStoreKey();

            var keys = await _subject.GetKeysFromCacheAsync();

            keys.Count().Should().Be(1);
            keys.Single().Id.Should().Be(id);
            _mockKeyStore.LoadKeysAsyncWasCalled.Should().BeFalse();
        }

        // AreAllKeysWithinInitializationDuration

        [Fact]
        public void AreAllKeysWithinInitializationDuration_should_ignore_retired_and_expired_keys()
        {
            {
                var key1 = CreateKey(_options.KeyRetirement);
                var key2 = CreateKey(_options.KeyExpiration);
                var key3 = CreateKey(_options.InitializationDuration.Add(-TimeSpan.FromSeconds(1)));

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key1, key2, key3 });

                result.Should().BeTrue();
            }
            {
                var key1 = CreateKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(1)));
                var key2 = CreateKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(1)));
                var key3 = CreateKey(_options.InitializationDuration.Add(-TimeSpan.FromSeconds(1)));

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key1, key2, key3 });

                result.Should().BeTrue();
            }
        }

        [Fact]
        public void AreAllKeysWithinInitializationDuration_for_new_keys_should_return_true()
        {
            {
                var key1 = CreateKey(_options.InitializationDuration.Add(-TimeSpan.FromSeconds(1)));

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key1 });

                result.Should().BeTrue();
            }
            {
                var key1 = CreateKey(_options.InitializationDuration);

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key1 });

                result.Should().BeTrue();
            }
            {
                var key1 = CreateKey();

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key1 });

                result.Should().BeTrue();
            }
            {
                var key1 = CreateKey(_options.InitializationDuration);
                var key2 = CreateKey(_options.InitializationDuration.Add(-TimeSpan.FromSeconds(1)));
                var key3 = CreateKey();

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key1, key2, key3 });

                result.Should().BeTrue();
            }
        }

        [Fact]
        public void AreAllKeysWithinInitializationDuration_for_older_keys_should_return_false()
        {
            {
                var key0 = CreateKey(_options.InitializationDuration.Add(TimeSpan.FromSeconds(1)));

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key0 });

                result.Should().BeFalse();
            }
            {
                var key0 = CreateKey(_options.InitializationDuration.Add(TimeSpan.FromSeconds(1)));
                var key1 = CreateKey(_options.InitializationDuration.Add(-TimeSpan.FromSeconds(1)));

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key0, key1 });

                result.Should().BeFalse();
            }
            {
                var key0 = CreateKey(_options.InitializationDuration.Add(TimeSpan.FromSeconds(1)));
                var key1 = CreateKey(_options.InitializationDuration);

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key0, key1 });

                result.Should().BeFalse();
            }
            {
                var key0 = CreateKey(_options.InitializationDuration.Add(TimeSpan.FromSeconds(1)));
                var key1 = CreateKey();

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key0, key1 });

                result.Should().BeFalse();
            }
            {
                var key0 = CreateKey(_options.InitializationDuration.Add(TimeSpan.FromSeconds(1)));
                var key1 = CreateKey(_options.InitializationDuration);
                var key2 = CreateKey(_options.InitializationDuration.Add(-TimeSpan.FromSeconds(1)));
                var key3 = CreateKey();

                var result = _subject.AreAllKeysWithinInitializationDuration(new[] { key0, key1, key2, key3 });

                result.Should().BeFalse();
            }
        }

        // FilterAndDeleteRetiredKeysAsync

        [Fact]
        public async Task FilterRetiredKeys_should_filter_retired_keys()
        {
            var key1 = CreateKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(1)));
            var key2 = CreateKey(_options.KeyRetirement);
            var key3 = CreateKey(_options.KeyRetirement.Subtract(TimeSpan.FromSeconds(1)));
            var key4 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));
            var key5 = CreateKey(_options.KeyActivationDelay);
            var key6 = CreateKey(_options.KeyActivationDelay.Subtract(TimeSpan.FromSeconds(1)));

            var result = await _subject.FilterAndDeleteRetiredKeysAsync(new[] { key1, key2, key3, key4, key5, key6 });

            result.Select(x => x.Id).Should().BeEquivalentTo(new[] { key3.Id, key4.Id, key5.Id, key6.Id });
        }

        [Fact]
        public async Task FilterRetiredKeys_should_delete_from_database()
        {
            _options.DeleteRetiredKeys = true;

            var key1 = CreateAndStoreKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(1)));
            var key2 = CreateAndStoreKey(_options.KeyRetirement);
            var key3 = CreateAndStoreKey(_options.KeyRetirement.Subtract(TimeSpan.FromSeconds(1)));
            var key4 = CreateAndStoreKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));
            var key5 = CreateAndStoreKey(_options.KeyActivationDelay);
            var key6 = CreateAndStoreKey(_options.KeyActivationDelay.Subtract(TimeSpan.FromSeconds(1)));

            var keys = await _subject.GetAllKeysAsync();

            _mockKeyStore.DeleteWasCalled.Should().BeTrue();
            _mockKeyStore.Keys.Select(x => x.Id).Should().BeEquivalentTo(new[] { key3, key4, key5, key6 });
        }

        [Fact]
        public async Task FilterRetiredKeys_when_delete_disabled_should_not_delete_from_database()
        {
            _options.DeleteRetiredKeys = false;

            var key1 = CreateAndStoreKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(1)));
            var key2 = CreateAndStoreKey(_options.KeyRetirement);
            var key3 = CreateAndStoreKey(_options.KeyRetirement.Subtract(TimeSpan.FromSeconds(1)));
            var key4 = CreateAndStoreKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));
            var key5 = CreateAndStoreKey(_options.KeyActivationDelay);
            var key6 = CreateAndStoreKey(_options.KeyActivationDelay.Subtract(TimeSpan.FromSeconds(1)));

            var keys = await _subject.GetAllKeysAsync();

            _mockKeyStore.DeleteWasCalled.Should().BeFalse();
            _mockKeyStore.Keys.Select(x => x.Id).Should().BeEquivalentTo(new[] { key1, key2, key3, key4, key5, key6 });
        }

        // FilterExpiredKeys

        [Fact]
        public void FilterExpiredKeys_should_filter_expired_keys()
        {
            var key1 = CreateKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(1)));
            var key2 = CreateKey(_options.KeyExpiration);
            var key3 = CreateKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1)));
            var key4 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));
            var key5 = CreateKey(_options.KeyActivationDelay);
            var key6 = CreateKey(_options.KeyActivationDelay.Subtract(TimeSpan.FromSeconds(1)));

            var result = _subject.FilterExpiredKeys(new[] { key1, key2, key3, key4, key5, key6 });

            result.Select(x => x.Id).Should().BeEquivalentTo(new[] { key3.Id, key4.Id, key5.Id, key6.Id });
        }

        // CacheKeysAsync

        [Fact]
        public async Task CacheKeysAsync_should_not_store_empty_keys()
        {
            {
                await _subject.CacheKeysAsync(null);

                _mockKeyStoreCache.StoreKeysAsyncWasCalled.Should().BeFalse();
            }

            {
                await _subject.CacheKeysAsync(new RsaKeyContainer[0]);

                _mockKeyStoreCache.StoreKeysAsyncWasCalled.Should().BeFalse();
            }
        }

        [Fact]
        public async Task CacheKeysAsync_should_store_keys_in_cache_with_normal_cache_duration()
        {
            var key1 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromMinutes(5)));
            var key2 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromMinutes(10)));

            await _subject.CacheKeysAsync(new[] { key1, key2 });

            _mockKeyStoreCache.StoreKeysAsyncWasCalled.Should().BeTrue();
            _mockKeyStoreCache.StoreKeysAsyncDuration.Should().Be(_options.KeyCacheDuration);

            _mockKeyStoreCache.Cache.Select(x => x.Id).Should().BeEquivalentTo(new[] { key1.Id, key2.Id });
        }

        [Fact]
        public async Task CacheKeysAsync_when_keys_are_new_should_use_initialization_duration()
        {
            var key1 = CreateKey();

            await _subject.CacheKeysAsync(new[] { key1 });

            _mockKeyStoreCache.StoreKeysAsyncWasCalled.Should().BeTrue();
            _mockKeyStoreCache.StoreKeysAsyncDuration.Should().Be(_options.InitializationKeyCacheDuration);
        }

        // GetKeysFromStoreAsync

        [Fact]
        public async Task GetKeysFromStoreAsync_should_use_store_and_cache_keys()
        {
            var key = CreateAndStoreKey();

            var keys = await _subject.GetKeysFromStoreAsync();

            keys.Should().NotBeNull();
            keys.Single().Id.Should().Be(key);
            _mockKeyStoreCache.GetKeysAsyncWasCalled.Should().BeFalse();
        }

        [Fact]
        public async Task GetKeysFromStoreAsync_should_filter_retired_keys()
        {
            var key1 = CreateAndStoreKey(TimeSpan.FromSeconds(10));
            var key2 = CreateAndStoreKey(TimeSpan.FromSeconds(5));
            var key3 = CreateAndStoreKey(-TimeSpan.FromSeconds(5));
            var key4 = CreateAndStoreKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(1)));
            var key5 = CreateAndStoreKey(_options.KeyRetirement.Add(TimeSpan.FromSeconds(5)));

            var keys = await _subject.GetKeysFromStoreAsync();

            keys.Select(x => x.Id).Should().BeEquivalentTo(new[] { key1, key2, key3, key4 });
        }

        [Fact]
        public async Task GetKeysFromStoreAsync_should_filter_null_keys()
        {
            var key1 = CreateAndStoreKey(TimeSpan.FromSeconds(10));
            _mockKeyStore.Keys.Add(null);

            var keys = await _subject.GetKeysFromStoreAsync();

            keys.Select(x => x.Id).Should().BeEquivalentTo(new[] { key1 });
        }

        // CreateNewKeyAndAddToCacheAsync

        [Fact]
        public async Task CreateNewKeyAndAddToCacheAsync_when_no_keys_should_store_and_return_new_key()
        {
            var (keys, key) = await _subject.CreateNewKeyAndAddToCacheAsync();

            _mockKeyStore.Keys.Single().Id.Should().Be(key.Id);
        }

        [Fact]
        public async Task CreateNewKeyAndAddToCacheAsync_when_existing_keys_should_store_and_return_active_key()
        {
            var key1 = CreateCacheAndStoreKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));

            var (keys, key) = await _subject.CreateNewKeyAndAddToCacheAsync();

            keys.Count().Should().Be(2);
            _mockKeyStore.Keys.Count.Should().Be(2);

            key.Id.Should().Be(key1);
        }

        [Fact]
        public async Task CreateNewKeyAndAddToCacheAsync_should_return_all_keys()
        {
            var key1 = CreateCacheAndStoreKey();

            var (keys, key) = await _subject.CreateNewKeyAndAddToCacheAsync();

            keys.Select(x => x.Id).Should().BeEquivalentTo(_mockKeyStore.Keys.Select(x => x.Id));
        }

        [Fact]
        public async Task CreateNewKeyAndAddToCacheAsync_when_keys_are_new_should_delay_for_initialization_and_synchronization_delay()
        {
            _options.InitializationSynchronizationDelay = TimeSpan.FromSeconds(5);

            var key1 = CreateCacheAndStoreKey();

            var sw = new Stopwatch();
            sw.Start();
            var (keys, key) = await _subject.CreateNewKeyAndAddToCacheAsync();
            sw.Stop();

            sw.Elapsed.Should().BeGreaterOrEqualTo(_options.InitializationSynchronizationDelay);

            keys.Select(x => x.Id).Should().BeEquivalentTo(_mockKeyStore.Keys.Select(x => x.Id));
        }

        [Fact]
        public async Task CreateNewKeyAndAddToCacheAsync_when_keys_are_old_should_not_delay_for_initialization_and_synchronization_delay()
        {
            _options.InitializationSynchronizationDelay = TimeSpan.FromSeconds(5);

            var key1 = CreateCacheAndStoreKey(_options.InitializationDuration.Add(TimeSpan.FromSeconds(1)));

            var sw = new Stopwatch();
            sw.Start();
            var (keys, key) = await _subject.CreateNewKeyAndAddToCacheAsync();
            sw.Stop();

            sw.Elapsed.Should().BeLessThan(_options.InitializationSynchronizationDelay);

            keys.Select(x => x.Id).Should().BeEquivalentTo(_mockKeyStore.Keys.Select(x => x.Id));
        }

        // GetActiveSigningKey

        [Fact]
        public void GetActiveSigningKey_for_no_keys_should_return_null()
        {
            {
                var key = _subject.GetActiveSigningKey(null);
                key.Should().BeNull();
            }
            {
                var key = _subject.GetActiveSigningKey(new RsaKeyContainer[0]);
                key.Should().BeNull();
            }
        }

        // GetActiveSigningKeyInternal

        [Fact]
        public void GetActiveSigningKeyInternal_should_return_the_oldest_active_key()
        {
            var key1 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(10)));
            var key2 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(5)));
            var key3 = CreateKey(_options.KeyActivationDelay.Add(-TimeSpan.FromSeconds(5)));
            var key4 = CreateKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(5)));

            var key = _subject.GetActiveSigningKeyInternal(new[] { key1, key2, key3, key4 });

            key.Should().NotBeNull();
            key.Id.Should().Be(key1.Id);
        }

        [Fact]
        public void GetActiveSigningKeyInternal_should_return_a_matching_key_type()
        {
            var rsaKey1 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(10)), KeyType.RSA);
            var x509Key1 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(20)), KeyType.X509);

            {
                _options.KeyType = KeyType.RSA;
                var key = _subject.GetActiveSigningKeyInternal(new[] { rsaKey1, x509Key1 });

                key.Should().NotBeNull();
                key.Id.Should().Be(rsaKey1.Id);
            }
            {
                _options.KeyType = KeyType.X509;
                var key = _subject.GetActiveSigningKeyInternal(new[] { rsaKey1, x509Key1 });

                key.Should().NotBeNull();
                key.Id.Should().Be(x509Key1.Id);
            }

            {
                var rsaKey2 = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(30)), KeyType.RSA);
                
                _options.KeyType = KeyType.RSA;
                var key = _subject.GetActiveSigningKeyInternal(new[] { rsaKey1, x509Key1, rsaKey2 });

                key.Should().NotBeNull();
                key.Id.Should().Be(rsaKey2.Id);
            }
        }

        // CanBeUsedForSigning

        [Fact]
        public void CanBeUsedForSigning_key_created_within_activity_delay_should_not_be_used_for_signing()
        {
            {
                var key = CreateKey(-TimeSpan.FromSeconds(1));

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeFalse();
            }

            {
                var key = CreateKey(TimeSpan.FromSeconds(1));

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeFalse();
            }

            {
                var key = CreateKey(_options.KeyActivationDelay.Subtract(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeFalse();
            }
        }

        [Fact]
        public void CanBeUsedForSigning_key_created_after_active_delay_should_be_used_for_signing()
        {
            {
                var key = CreateKey(_options.KeyActivationDelay);

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyExpiration);

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeTrue();
            }
        }

        [Fact]
        public void CanBeUsedForSigning_key_older_than_expiration_should_not_be_used_for_signing()
        {
            {
                var key = CreateKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeFalse();
            }
        }

        [Fact]
        public void CanBeUsedForSigning_ignoring_activity_delay_key_created_within_activity_delay_should_be_used_for_signing()
        {
            {
                var key = CreateKey(-TimeSpan.FromSeconds(1));

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(TimeSpan.FromSeconds(1));

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyActivationDelay.Subtract(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeTrue();
            }
        }

        [Fact]
        public void CanBeUsedForSigning_ignoring_activity_delay_key_created_after_active_delay_should_be_used_for_signing()
        {
            {
                var key = CreateKey(_options.KeyActivationDelay);

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeTrue();
            }

            {
                var key = CreateKey(_options.KeyExpiration);

                var result = _subject.CanBeUsedForSigning(key);

                result.Should().BeTrue();
            }
        }

        [Fact]
        public void CanBeUsedForSigning_ignoring_activity_delay_key_older_than_expiration_should_not_be_used_for_signing()
        {
            {
                var key = CreateKey(_options.KeyExpiration.Add(TimeSpan.FromSeconds(1)));

                var result = _subject.CanBeUsedForSigning(key, true);

                result.Should().BeFalse();
            }
        }

        // CreateAndStoreNewKeyAsync

        [Fact]
        public async Task CreateAndStoreNewKeyAsync_should_create_and_store_and_return_key()
        {
            var result = await _subject.CreateAndStoreNewKeyAsync();

            _mockKeyProtector.ProtectWasCalled.Should().BeTrue();
            _mockKeyStore.Keys.Count.Should().Be(1);
            _mockKeyStore.Keys.Single().Id.Should().Be(result.Id);
            result.Created.Should().Be(_mockClock.UtcNow.DateTime);
        }

        // IsKeyRotationRequired

        [Fact]
        public void IsKeyRotationRequired_when_no_keys_should_be_true()
        {
            {
                var result = _subject.IsKeyRotationRequired(null);
                result.Should().BeTrue();
            }
            {
                var result = _subject.IsKeyRotationRequired(new RsaKeyContainer[0]);
                result.Should().BeTrue();
            }
        }

        [Fact]
        public void IsKeyRotationRequired_when_no_active_key_should_be_true()
        {
            {
                var keys = new RsaKeyContainer[] {
                    CreateKey(_options.KeyRetirement.Add(TimeSpan.FromDays(1))),
                };
                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeTrue();
            }

            {
                var keys = new[] {
                    CreateKey(_options.KeyExpiration.Add(TimeSpan.FromDays(1))),
                };

                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeTrue();
            }
        }

        [Fact]
        public void IsKeyRotationRequired_when_active_key_is_not_about_to_expire_should_be_false()
        {
            var keys = new[] {
                CreateKey(_options.KeyExpiration.Subtract(_options.KeyActivationDelay.Add(TimeSpan.FromSeconds(1)))),
            };

            var result = _subject.IsKeyRotationRequired(keys);
            result.Should().BeFalse();
        }

        [Fact]
        public void IsKeyRotationRequired_when_active_key_is_about_to_expire_should_be_true()
        {
            {
                var keys = new[] {
                    CreateKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1))),
                };

                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeTrue();
            }
            {
                var keys = new[] {
                CreateKey(_options.KeyExpiration.Subtract(_options.KeyActivationDelay)),
                };

                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeTrue();
            }
        }

        [Fact]
        public void IsKeyRotationRequired_when_younger_keys_exist_should_be_false()
        {
            {
                var keys = new[] {
                    CreateKey(_options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1))), // active key about to expire
                    CreateKey() // very new key
                };

                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeFalse();
            }
            {
                var keys = new[] {
                    CreateKey(_options.KeyActivationDelay), // active key not about to expire
                    CreateKey() // very new key
                };

                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeFalse();
            }
        }

        [Fact]
        public void IsKeyRotationRequired_when_younger_keys_are_close_to_expiration_should_be_true()
        {
            {
                var age = _options.KeyExpiration.Subtract(TimeSpan.FromSeconds(1));
                var keys = new[] {
                    CreateKey(age), // active key about to expire
                    CreateKey(age.Subtract(TimeSpan.FromSeconds(1)))  // newer, but still close to expiration
                };

                var result = _subject.IsKeyRotationRequired(keys);
                result.Should().BeTrue();
            }
        }
    }
}
